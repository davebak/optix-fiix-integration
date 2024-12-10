#region Using directives
using System;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.DataLogger;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.NativeUI;
using FTOptix.UI;
using FTOptix.CoreBase;
using FTOptix.SQLiteStore;
using FTOptix.Store;
using FTOptix.OPCUAServer;
using FTOptix.RAEtherNetIP;
using FTOptix.CommunicationDriver;
using FTOptix.Core;
using System.Collections.Generic;
using System.Linq;
using HttpAPIGateway;
using System.Globalization;
using System.Threading;
using Newtonsoft.Json;
using System.IO;
using System.Text;
using System.Net.Http;
using System.Security.Cryptography;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Net.Http.Json;
//using Newtonsoft.Json.Linq;
using FTOptix.WebUI;
using FTOptix.EventLogger;
using System.Runtime.CompilerServices;
using System.Data;
using static System.Formats.Asn1.AsnWriter;
using System.Reflection.Metadata;
using System.Net;
using Gpe.Integration.Fiix.Connector.Models;
using Gpe.Integration.Fiix.Connector.Configuration;
using Gpe.Integration.Fiix.Connector.Services;
using Gpe.Integration.Fiix.Connector.Models.Utilities;
using Gpe.Integration.Fiix.Connector;
using Gpe.Integration.Fiix.Connector.Services.ARP;
using Gpe.Integration.Fiix.Connector.Models.AssetRiskPredictor;
using System.Reflection;
#endregion
/*
Fiix Gateway runtime script to manage Push Agent and meter reading data sending.
=============================================================

Disclaimer of Warranty
THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT ARE PROVIDED "AS IS" WITHOUT WARRANTIES OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION, ALL IMPLIED WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE, NON-INFRINGEMENT OR OTHER VIOLATION OF RIGHTS. ROCKWELL AUTOMATION DOES NOT WARRANT OR MAKE ANY REPRESENTATIONS REGARDING THE USE, VALIDITY, ACCURACY, OR RELIABILITY OF, OR THE RESULTS OF ANY USE OF, OR OTHERWISE RESPECTING, THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT OR ANY WEB SITE LINKED TO THIS DOCUMENT 

Limitation of Liability
UNDER NO CIRCUMSTANCE (INCLUDING NEGLIGENCE AND TO THE FULLEST EXTEND PERMITTED BY APPLICABLE LAW) WILL ROCKWELL AUTOMATION BE LIABLE FOR ANY DIRECT, INDIRECT, SPECIAL, INCIDENTAL, PUNITIVE OR CONSEQUENTIAL DAMAGES (INCLUDING WITHOUT LIMITATION, BUSINESS INTERRUPTION, DELAYS, LOSS OF DATA OR PROFIT) ARISING OUT OF THE USE OR THE INABILITY TO USE THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT EVEN IF ROCKWELL AUTOMATION HAS BEEN ADVISED OF THE POSSIBILITY OF SUCH DAMAGES. IF USE OF SUCH MATERIALS RESULTS IN THE NEED FOR SERVICING, REPAIR OR CORRECTION OF USER EQUIPMENT OR DATA, USER ASSUMES ANY COSTS ASSOCIATED THEREWITH.

Copyright © Rockwell Automation, Inc.  All Rights Reserved. 

=============================================================
*/
public class FiixGatewayRuntimeLogic : BaseNetLogic
{
    // Runtime Store_and_Send functioin base on PushAgent with changes: added mqtt along with http; Added ARP data support, calcualate stastics summary data.
    const int ARPCalculationTimePeriodInSecond = 60;

    public override void Start()
    {
        Log.Verbose1("FiixGatewayRuntime", "Start Gateway Runtime.");
        CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("en-US");
        IUANode designTimeLogic = LogicObject.Owner.Find("FiixGatewayDesigntimeLogic");

        // Update Assets status and properties if Fiix URL is set
        isARPenabled = designTimeLogic.GetVariable("Cfg_ARP_ApiKey").Value != "" && designTimeLogic.GetVariable("Cfg_ARP_ApiSecret").Value != "" && (bool)LogicObject.GetVariable("Cfg_ARP_enabled").Value;
        arpDataSendInterval = LogicObject.GetVariable("Cfg_ARP_SendInterval") != null ? (int)LogicObject.GetVariable("Cfg_ARP_SendInterval").Value : 3600;
        
        bool isFiixConfigured = !(designTimeLogic == null || designTimeLogic.GetVariable("Cfg_FiixURL") == null || designTimeLogic.GetVariable("Cfg_FiixURL").Value == "");
        if (!isFiixConfigured && !isARPenabled)
        {
            Log.Warning("Fiix Gateway", "Fiix connection is not configured.");
            return;
        }

        if (isFiixConfigured)
        {
            SyncAssets();

            int AssetStatusAutoUpdatePeriod = LogicObject.GetVariable("Set_AssetStatusAutoUpdate").Value;
            if (AssetStatusAutoUpdatePeriod > 0)
            {
                PeriodicTask assetAutoUpdateTask = new PeriodicTask(SyncAssets, AssetStatusAutoUpdatePeriod, LogicObject);
                assetAutoUpdateTask.Start();
            }
        }
        // Added Fiix ARP support July 2024, can run without Fiix instance
        if (isARPenabled)
        {
            // Initial ARP API Service with singleton HttpClient
            ArpApiConnectionConfiguration arpconfig = GatewayUtils.GetARP_APIConnectionConfiguration();
            if (arpconfig != null)
            {
                arpHttpHandlerSingleton = ArpHttpClientHandler.Create(arpconfig, true);
            }
            else Log.Error("Fiix Gateway", "Get Fiix ARP connection configuration error");

            // Check next 0 second mark, run data summary close to minute start
            int currentSecond = DateTime.Now.Second;
            ARPDelayedTask = new DelayedTask(StartSummaryCycle, (60 - currentSecond) * 1000, LogicObject);
            ARPDelayedTask.Start();
        }

        // Prepare PushAgent for meter reading sending. Initial gateway stores and buffer if DataLogger is set and VariableToLog is not empty
        DataLogger metereadingLogger = (DataLogger)InformationModel.Get(LogicObject.GetVariable("Cfg_DataLogger").Value);
        if (metereadingLogger == null || (metereadingLogger.VariablesToLog.Count == 0 && !isARPenabled ))
        {
            Log.Info("Fiix Gateway", "Both meter Reading auto-send and ARP is not configured, push agent will not be initiated.");
            return;
        }

        enableGatewaySend = LogicObject.GetVariable("Set_MeterReadingStoreAndSend").Value;
        LogicObject.GetVariable("Set_MeterReadingStoreAndSend").VariableChange += ChangeSending;

        try
        {
            cancellationTokenSource = new CancellationTokenSource();
            LoadPushAgentConfiguration();
            ConfigureStores();
            ConfigureDataLoggerRecordPuller();
        }
        catch (Exception e)
        {
            Log.Error("PushAgent", $"Unable to initialize PushAgent, an error occurred: {e.Message}.");
            throw;
        }
        EnablePushAgentSending();
    }

    public override void Stop()
    {
        Log.Verbose1("PushAgent", "Stop push agent.");
        DisablePushAgentSending();
        IUANode tempDB = (SQLiteStore)LogicObject.Find("tempDB");
        if (tempDB != null) tempDB.Delete();

        GatewayUtils.DisposeFiixCMMS_SingletonAPIService();
        GatewayUtils.DisposeFiixARP_SingletonAPIService();
        // Added Fiix ARP support July 2024,
        ARP_PeriodicTask?.Dispose();
        ARPDelayedTask?.Dispose();
    }

    public void ChangeSending(object sender, VariableChangeEventArgs e)
    {
        enableGatewaySend = e.NewValue;

        //if (e.NewValue) EnablePushAgentSending();
        //else DisablePushAgentSending();
    }

    private void EnablePushAgentSending()
    {
        Log.Verbose1("GatewayRuntime", "Start Fetch Timer.");
        StartFetchTimer();
    }

    private void DisablePushAgentSending()
    {
        Log.Verbose1("PushAgent", "Stop push agent.");

        if (cancellationTokenSource!=null) cancellationTokenSource.Cancel();
        if (dataLoggerRecordPuller == null) return;
        try
        {
            dataLoggerRecordPuller.StopPullTask();
            lock (dataFetchLock)
            {
                dataFetchTask.Cancel();
            }
        }
        catch (Exception e)
        {
            Log.Warning("PushAgent", $"Error occurred during stoping push agent: {e.Message}");
        }
    }

    [ExportMethod]
    public void ClearPushAgentTempStore()
    {
        if (pushAgentStore != null)
        {
            try
            {
                pushAgentStore.DeleteRecords(100000000);
                Log.Info("Fiix PushAgent", "Deleting PushAgent Buffer TempStore records.");
            }
            catch { }
        }
    }

    [ExportMethod]
    public void ClearDataLoggerStore()
    {
        if (dataLoggerStore != null)
        {
            try
            {
                dataLoggerStore.DeleteRecords(100000000);
                dataLoggerStore.DeleteTemporaryTable();      // When user change variable DataType after creation, temporary table might stuck with old data with cast data type error
                Log.Info("Fiix PushAgent", "Deleting DataLogger Store records.");
            }
            catch (Exception ex)
            { Log.Info("Fiix Gateway", "No record in DataStore to be cleared"); }
        }
    }

    [ExportMethod]
    public void SyncAssets()
    {
        // Used in Runtime to update Asset properties only, will pause when user trying to change Online status.
        //Log.Info("Update Asset Status.");
        if (!LogicObject.GetVariable("Sts_AssetStatusUpdatePause").Value)
        {
            LongRunningTask syncAssetTask = new LongRunningTask(UpdateAssets, LogicObject);
            syncAssetTask.Start();
        }
    }

    private void UpdateAssets()
    {
        // Used in Runtime to update Asset properties only, will pause when user trying to change Online status.
        //Log.Info("Update Asset Status.");
        if (!LogicObject.GetVariable("Sts_AssetStatusUpdatePause").Value)
        {
            GatewayUtils.SyncAssetTree(false);
        }
    }

    // Used to update Online status only, as backup only
    void UpdateOnlineStatus()
    {
        IUANode designTimeLogic = LogicObject.Owner.Find("FiixGatewayDesigntimeLogic");
        if (designTimeLogic == null) {
            Log.Error("Fiix Gateway", "Update Online Status error: Could not find DesignTimeLogic to get configuration");
            return;
        }
        IUANode modelFolder = LogicObject.Owner.Owner.Find("Assets");
        IUANode AssetType = LogicObject.Owner.Find("Asset");
        int updateSiteCount = 0, updateAssetCount = 0;
        CmmsApiService apiService = GatewayUtils.GetFiixCMMS_SingletonAPIService();

        // Update Sites
        string filterName = (string)designTimeLogic.GetVariable("Set_FilterSiteNames").Value;
        
        ApiResponseModel responseModel = apiService.FindAssetsBatch(true, -1, "", -1, filterName, true).Result;
        Fiix_Asset[] fiixSites = responseModel.objects.OfType<Fiix_Asset>().ToArray();
        LogicObject.GetVariable("Sts_LastExecutionDatetime").Value = DateTime.Now;
        if (!responseModel.Success || fiixSites == null)
        {
            LogicObject.GetVariable("Sts_LastExecutionResult").Value = "Get Fiix Sites online status with no result.";
            if (!responseModel.Success) Log.Error("Fiix Gateway", "Update Online Status error:" + responseModel.ErrorMessage);
            return;
        }
        List<IUANode> sites = modelFolder.Children.Cast<IUANode>().ToList();

        foreach (var fiixsite in fiixSites)
        {
            IUANode newSite;

            if (sites.Exists(site => (fiixsite.id == (int)site.GetVariable("id").Value) || fiixsite.strName == site.GetVariable("strName").Value))
            {
                newSite = sites.Find(site => fiixsite.id == site.GetVariable("id").Value);
                newSite.GetVariable("bolIsOnline").Value = Convert.ToBoolean(fiixsite.bolIsOnline);
                newSite.GetVariable("dtUpdated").Value = DateTimeOffset.FromUnixTimeMilliseconds(fiixsite.intUpdated).DateTime;
                updateSiteCount++;
            }
        }

        // Sync all Assets with isSite is false
        sites = modelFolder.Children.Cast<IUANode>().ToList();
        //if (modelFolder.Find("Template") != null) sites.Remove(modelFolder.Get("Template"));

        string sfilterName = (string)designTimeLogic.GetVariable("Set_FilterAssetNames").Value;
        Fiix_Asset[] fiixFacilities;
        if (sfilterName != null && sfilterName.Trim() == "" && !(bool)designTimeLogic.GetVariable("Set_FilterEnabledAssetCategoryOnly").Value)
        {
            fiixFacilities = apiService.FindAssetsBatch(false, -1, "", -1, sfilterName, true).Result.objects.OfType<Fiix_Asset>().ToArray();
        }
        else
        {
            // Seperate Facility/Location with Equipment/Tool assets for filtering by name function. Get Equipment/Tool with text included in filter only.
            Fiix_Asset[] pureAssets = apiService.FindAssetsBatch(false, 1, "", -1, sfilterName, true).Result.objects.OfType<Fiix_Asset>().ToArray();
            Fiix_Asset[] nonAssets = apiService.FindAssetsBatch(false, 2, "", -1, "", true).Result.objects.OfType<Fiix_Asset>().ToArray();
            if (nonAssets == null)
            {
                if (pureAssets != null) fiixFacilities = pureAssets.ToArray();
                else fiixFacilities = null;
            }
            else
            {
                if (pureAssets != null && pureAssets.Length > 0) fiixFacilities = nonAssets.Concat(pureAssets).ToArray();
                else fiixFacilities = nonAssets;
            }
        }

        if (fiixFacilities == null)
        {
            LogicObject.GetVariable("Sts_LastExecutionResult").Value += ", Get Fiix Facilities online status with no result.";
            return;
        }

        // Loop through sites to nested call find fiixFacility with parentID
        foreach (IUANode site in sites) AddUpdateFacilityByLocation(site, fiixFacilities);

        LogicObject.GetVariable("Sts_LastExecutionResult").Value = updateSiteCount + " sites status and " + updateAssetCount + " assets online status updated";

        void AddUpdateFacilityByLocation(IUANode parentNode, Fiix_Asset[] assets)
        {
            if (parentNode == null) return;
            // Get existing object nodes children
            var existingChildren = parentNode.Children.Cast<IUANode>().ToList();
            existingChildren.RemoveAll(x => x.NodeClass != NodeClass.Object || x.BrowseName.Contains("DesignTimeLogic"));

            foreach (Fiix_Asset asset in assets)
            {
                if (asset.intAssetLocationID == (int)parentNode.GetVariable("id").Value)
                {
                    // Check if the child already existing by id
                    IUANode currentNode = null;

                    foreach (IUANode childNode in existingChildren)
                    {
                        if ((int)childNode.GetVariable("id").Value == asset.id || childNode.GetVariable("strName").Value == asset.strName)
                        // node with the same id exist, update
                        {
                            childNode.GetVariable("bolIsOnline").Value = Convert.ToBoolean(asset.bolIsOnline);
                            childNode.GetVariable("dtUpdated").Value = DateTimeOffset.FromUnixTimeMilliseconds(asset.intUpdated).DateTime;
                            updateAssetCount++;
                            existingChildren.Remove(childNode);
                            currentNode = childNode;
                            break;
                        }
                    };
                    AddUpdateFacilityByLocation(currentNode, assets);
                }
            }
        }
    }

    private void ConfigureDataLoggerRecordPuller()
    {
        int dataLoggerPullPeriod = LogicObject.GetVariable("Cfg_DataLoggerPullTime").Value; // Period used to pull new data from the DataLogger

        if (pushAgentConfigurationParameters.preserveDataLoggerHistory)
        {
            dataLoggerRecordPuller = new DataLoggerRecordPuller(LogicObject,
                                                                LogicObject.GetVariable("Cfg_DataLogger").Value,
                                                                pushAgentStore,
                                                                statusStoreWrapper,
                                                                dataLoggerStore,
                                                                pushAgentConfigurationParameters.preserveDataLoggerHistory,
                                                                pushAgentConfigurationParameters.pushFullSample,
                                                                dataLoggerPullPeriod,
                                                                pushAgentConfigurationParameters.dataLogger.VariablesToLog.ToList().Count);
        }
        else
        {
            dataLoggerRecordPuller = new DataLoggerRecordPuller(LogicObject,
                                                                LogicObject.GetVariable("Cfg_DataLogger").Value,
                                                                pushAgentStore,
                                                                dataLoggerStore,
                                                                pushAgentConfigurationParameters.preserveDataLoggerHistory,
                                                                pushAgentConfigurationParameters.pushFullSample,
                                                                dataLoggerPullPeriod,
                                                                pushAgentConfigurationParameters.dataLogger.VariablesToLog.ToList().Count);
        }
    }

    private void ConfigureStores()
    {
        string pushAgentStoreBrowseName = "PushAgentStore";
        string pushAgentFilename = "push_agent_store";
        CreatePushAgentStore(pushAgentStoreBrowseName, pushAgentFilename);

        var variableLogOpCode = pushAgentConfigurationParameters.dataLogger.GetVariable("LogVariableOperationCode");
        insertOpCode = variableLogOpCode != null ? (bool)variableLogOpCode.Value : false;

        var variableTimestamp = pushAgentConfigurationParameters.dataLogger.GetVariable("LogVariableTimestamp");
        insertVariableTimestamp = variableTimestamp != null ? (bool)variableTimestamp.Value : false;

        var logLocalTimestamp = pushAgentConfigurationParameters.dataLogger.GetVariable("LogLocalTime");
        logLocalTime = logLocalTimestamp != null ? (bool)logLocalTimestamp.Value : false;

        jsonCreator = new JSONBuilder(insertOpCode, insertVariableTimestamp, logLocalTime);

        dataLoggerStore = new DataLoggerStoreWrapper(InformationModel.Get<FTOptix.Store.Store>(pushAgentConfigurationParameters.dataLogger.Store),
                                            GetDataLoggerTableName(),
                                            pushAgentConfigurationParameters.dataLogger.VariablesToLog.ToList(),
                                            insertOpCode,
                                            insertVariableTimestamp,
                                            logLocalTime);

        if (!pushAgentConfigurationParameters.pushFullSample)
        {
            string tableName = "PushAgentTableRowPerVariable";
            pushAgentStore = new PushAgentStoreRowPerVariableWrapper(LogicObject.Get<SQLiteStore>(pushAgentStoreBrowseName),
                                                                     tableName,
                                                                     insertOpCode);
        }
        else
        {
            string tableName = "PushAgentTableDataLogger";
            pushAgentStore = new PushAgentStoreDataLoggerWrapper(LogicObject.Get<SQLiteStore>(pushAgentStoreBrowseName),
                                                                tableName,
                                                                pushAgentConfigurationParameters.dataLogger.VariablesToLog.ToList(),
                                                                insertOpCode,
                                                                insertVariableTimestamp,
                                                                logLocalTime);
            if (GetMaximumRecordsPerPacket() != 1)
            {
                Log.Warning("PushAgent", "For PushByRow mode maximum one row per packet is supported. Setting value to 1.");
                LogicObject.GetVariable("Cfg_MaximumItemsPerPacket").Value = 1;
            }
        }

        if (pushAgentConfigurationParameters.preserveDataLoggerHistory)
        {
            string tableName = "DataLoggerStatusStore";
            statusStoreWrapper = new DataLoggerStatusStoreWrapper(LogicObject.Get<SQLiteStore>(pushAgentStoreBrowseName),
                                                                                            tableName,
                                                                                            pushAgentConfigurationParameters.dataLogger.VariablesToLog.ToList(),
                                                                                            insertOpCode,
                                                                                            insertVariableTimestamp);
        }
    }

    private void StartFetchTimer()
    {
        if (cancellationTokenSource.IsCancellationRequested)
            return;
        try
        {
            // Set the correct timeout by checking number of records to be sent
            if (pushAgentStore.RecordsCount() >= GetMaximumRecordsPerPacket())
                nextRestartTimeout = GetMinimumPublishTime();
            else
                nextRestartTimeout = GetMaximumPublishTime();
            dataFetchTask = new DelayedTask(OnFetchRequired, nextRestartTimeout, LogicObject);

            lock (dataFetchLock)
            {
                dataFetchTask.Start();
            }
            Log.Verbose1("PushAgent", $"Fetching next data in {nextRestartTimeout} ms.");
        }
        catch (Exception e)
        {
            OnFetchError("Set time delay on fetch data from temp store" + e.Message);
        }
    }

    private void OnFetchRequired()
    {
        if (pushAgentStore.RecordsCount() > 0 && enableGatewaySend)
            FetchData();
        //  else
        StartFetchTimer();
    }

    private void FetchData()
    {
        if (cancellationTokenSource.IsCancellationRequested)
            return;

        Log.Verbose1("PushAgent", "Fetching data from push agent temporary store");
        var records = GetRecordsToSend();
        List<VariableRecord> arpRecords = new List<VariableRecord>();
        List<VariableRecord> meterRecords = new List<VariableRecord>();

        if (records.Count > 0)
        {
            // Publish(GenerateJSON(records));
            // clientID is replace with "Fiix" as it is used in the restAPI
            var now = DateTime.Now;
            if (pushAgentConfigurationParameters.pushFullSample)
            {
                pendingSendPacket = new DataLoggerRowPacket(now, "Fiix", records.Cast<DataLoggerRecord>().ToList());
            }
            else  // Fiix Gateway always uses variable package
            {
                arpRecords = records.Cast<VariableRecord>().ToList().FindAll(var => var.variableId.Contains("FiixARP1_JSON"));
                meterRecords = records.Cast<VariableRecord>().ToList().FindAll(var => !var.variableId.Contains("FiixARP1_JSON")); ;
                Log.Info("Fiix Gateway", meterRecords.Count + " MeterReading records and " + arpRecords.Count + " ARP records being sent from PushAgentTempStore: " + "; Store has total " + pushAgentStore.RecordsCount() + " records.");
                recordToSendCount = meterRecords.Count;
                pendingSendPacket = new VariablePacket(now, "Fiix", meterRecords);
                pendingARPSendPacket = new VariablePacket(now, "Fiix", arpRecords);
            }
            LogicObject.GetVariable("Sts_PushAgentLastSendResult").Value = "";
            Publish(GatewayUtils.GetMeterReadingBatchPayloadFromLogRecords(meterRecords));
            PublishARP(arpRecords);
        }
    }

    private List<Record> GetRecordsToSend()
    {
        List<Record> result = null;
        try
        {
            result = pushAgentStore.QueryOlderEntries(GetMaximumRecordsPerPacket());
        }
        catch (Exception e)
        {
            OnFetchError("Get Agent Store records to send " + e.Message);
        }
        return result;
    }

    private string GenerateJSON(List<Record> records)  // Not used in Fiix JSON composition for special data format required
    {
        var now = DateTime.Now;
        var clientId = pushAgentConfigurationParameters.mqttConfigurationParameters.clientId;

        if (pushAgentConfigurationParameters.pushFullSample)
        {
            pendingSendPacket = new DataLoggerRowPacket(now, clientId, records.Cast<DataLoggerRecord>().ToList());
            return jsonCreator.CreatePacketFormatJSON((DataLoggerRowPacket)pendingSendPacket);
        }
        else
        {
            pendingSendPacket = new VariablePacket(now, clientId, records.Cast<VariableRecord>().ToList());
            return jsonCreator.CreatePacketFormatJSON((VariablePacket)pendingSendPacket);
        }
    }

    private void Publish(string json)
    {
        try
        {
            // ==== Replace following mqtt publich with http post for http client =====
            //mqttClientConnector.PublishAsync(json,
            //                                 pushAgentConfigurationParameters.mqttConfigurationParameters.brokerTopic,
            //                                 false,
            //                                 pushAgentConfigurationParameters.mqttConfigurationParameters.qos)
            //    .Wait();

            // DeleteRecordsFromTempStore();
            if (json.Trim() == "")
            {
                DeleteRecordsFromTempStore();
                return;
            }
            LogicObject.GetVariable("Sts_PushAgentLastSendDatetime").Value = DateTime.Now;
            CmmsApiService apiService = GatewayUtils.GetFiixCMMS_SingletonAPIService();
            ApiResponseModel result = apiService.AddMeterReadingBatch(json).Result;
            if (result.Success)
            {
                DeleteRecordsFromTempStore();
                LogicObject.GetVariable("Sts_PushAgentLastSendResult").Value = recordToSendCount + " reading sent,";
            }
            else
            {
                LogicObject.GetVariable("Sts_PushAgentLastSendResult").Value = "MeterReading send failed,";
                Log.Error("Fiix Gateway", "MeterReading send failed with error: " + result.ErrorMessage);
            }
            // StartFetchTimer();
        }
        catch (OperationCanceledException)
        {
            // empty
            LogicObject.GetVariable("Sts_PushAgentLastSendResult").Value = "Canceled,";
        }
        catch (Exception e)
        {
            Log.Error("PushAgent", $"Error occurred during publishing: {e.Message}");
            LogicObject.GetVariable("Sts_PushAgentLastSendResult").Value = "Error";
            // StartFetchTimer();
        }
    }

    private void PublishARP(List<VariableRecord> records)
    {
        if (records.Count == 0)
        {
            DeleteARPRecordsFromTempStore();
            return;
        }
        LogicObject.GetVariable("Sts_PushAgentLastSendDatetime").Value = DateTime.Now;
        ArpApiService arpAPIService = GatewayUtils.GetFiixARP_SingletonAPIService();
        //if (!fiixARPHttpClient.hasToken)
        //{
        //    Log.Warning("Fiix ARP function", "failed to get API token.");
        //    return;
        //}
        int goodCount = 0, badCount = 0, emptyValueCount = 0;
        bool atleastOneSucceed = false;

        foreach (var record in records)
        {
            if (!record.variableId.Contains("FiixARP1_JSON") || record.serializedValue == null || record.serializedValue == "")
            {
                emptyValueCount++;
                continue;
            }
            string json = record.serializedValue.ToString();
            try
            {
                var result = arpAPIService.AddSensorData(json);
                if (result != null && result.Result!=null && result.Result.Contains("Success"))
                {
                    goodCount++;
                    atleastOneSucceed = true;
                    Log.Info("Fiix ARP data publish", "ARP data of " + record.variableId + " is sent successfully. Content: " + record.serializedValue.ToString());
                }
                else
                {
                    badCount++;
                    Log.Warning("Fiix ARP data publish", "ARP data of " + record.variableId + " is sent unsuccessfully. Content: " + record.serializedValue.ToString());
                }
            }
            catch (OperationCanceledException)
            {
                // empty
                Log.Info("Fiix ARP function", "ARP Send for record " + record.variableId + " Canceled.");
            }
            catch (Exception e)
            {
                Log.Warning("Fiix ARP function", $"Error occurred during publishing ARP Data for " + record.variableId + ": {e.Message}");
            }
        }
        if (atleastOneSucceed || emptyValueCount == records.Count) DeleteARPRecordsFromTempStore();
        LogicObject.GetVariable("Sts_PushAgentLastSendResult").Value += " " + goodCount + " ARP Sent " + badCount + " failed";
    }

    private void DeleteRecordsFromTempStore()
    {
        try
        {
            Log.Verbose1("PushAgent", "Delete records from push agent temporary store.");
            if (pushAgentConfigurationParameters.pushFullSample)
                pushAgentStore.DeleteRecords(((DataLoggerRowPacket)pendingSendPacket).records.Count);
            else
                pushAgentStore.DeleteRecords(((VariablePacket)pendingSendPacket).records.Count);
            pendingSendPacket = null;
        }
        catch (Exception e)
        {
            OnFetchError("Delete records from temp agent store" + e.Message);
        }
    }

    // Added July 2024 to support ARP, to delete ARP JSON data only from temp store.
    private void DeleteARPRecordsFromTempStore()
    {
        try
        {
            Log.Verbose1("PushAgent", "Delete ARP records from push agent temporary store.");
            if (pushAgentConfigurationParameters.pushFullSample)
                return ;
            else
                if (pendingARPSendPacket != null && ((VariablePacket)pendingARPSendPacket).records != null)
                ((PushAgentStoreRowPerVariableWrapper)pushAgentStore).DeleteARPRecords(((VariablePacket)pendingARPSendPacket).records.Count);
                pendingARPSendPacket = null;
        }
        catch (Exception e)
        {
            OnFetchError("Delete ARP records from temp agent store " + e.Message);
        }
    }

    private void OnFetchError(string message)
    {
        Log.Error("PushAgent", $"Error while fetching data: {message}.");
        dataLoggerRecordPuller.StopPullTask();
        lock (dataFetchLock)
        {
            dataFetchTask.Cancel();
        }
    }

    //private void LoadMQTTConfiguration()
    //{
    //    pushAgentConfigurationParameters.mqttConfigurationParameters = new MQTTConfigurationParameters
    //    {
    //        clientId = LogicObject.GetVariable("ClientId").Value,
    //        brokerIPAddress = LogicObject.GetVariable("BrokerIPAddress").Value,
    //        brokerPort = LogicObject.GetVariable("BrokerPort").Value,
    //        brokerTopic = LogicObject.GetVariable("BrokerTopic").Value,
    //        qos = LogicObject.GetVariable("QoS").Value,
    //        useSSL = LogicObject.GetVariable("UseSSL").Value,
    //        pathCACert = ResourceUriValueToAbsoluteFilePath(LogicObject.GetVariable("UseSSL/CACert").Value),
    //        pathClientCert = ResourceUriValueToAbsoluteFilePath(LogicObject.GetVariable("UseSSL/ClientCert").Value),
    //        passwordClientCert = LogicObject.GetVariable("UseSSL/ClientCertPassword").Value,
    //        username = LogicObject.GetVariable("Username").Value,
    //        password = LogicObject.GetVariable("Password").Value
    //    };
    //}

    // For Fiix MeterReading, use Variable Packet only, ignore "PushFullSample" setting from standard PushAgent.
    private void LoadPushAgentConfiguration()
    {
        pushAgentConfigurationParameters = new PushAgentConfigurationParameters();

        try
        {
            pushAgentConfigurationParameters.dataLogger = GetDataLogger();
            // pushAgentConfigurationParameters.pushFullSample = LogicObject.GetVariable("Cfg_PushFullSample").Value;
            // Ignore PushFullSample setting which is not applicable to MeterReading in Fiix
            // use Variable Packet only
            pushAgentConfigurationParameters.pushFullSample = false;
            pushAgentConfigurationParameters.preserveDataLoggerHistory = LogicObject.GetVariable("Cfg_PreserveDataLoggerHistory").Value;

            //  Added for ARP support, 7/2024, when ARP is enabled, always perserve data history
            if (LogicObject.GetVariable("Cfg_ARP_enabled").Value ?? false) pushAgentConfigurationParameters.preserveDataLoggerHistory = true;
        }
        catch (Exception e)
        {
            throw new CoreConfigurationException("PushAgent: Configuration error", e);
        }

    }

    //private void CheckMQTTParameters()
    //{
    //    if (pushAgentConfigurationParameters.mqttConfigurationParameters.useSSL && string.IsNullOrWhiteSpace(pushAgentConfigurationParameters.mqttConfigurationParameters.pathCACert))
    //    {
    //        Log.Warning("PushAgent", "CA certificate path is not set. Set CA certificate path or install CA certificate in the system.");
    //    }
    //    var qos = pushAgentConfigurationParameters.mqttConfigurationParameters.qos;
    //    if (qos < 0 || qos > 2)
    //    {
    //        Log.Warning("PushAgent", "QoS Values valid are 0, 1, 2.");
    //    }
    //}

    private int GetMaximumRecordsPerPacket()
    {
        return LogicObject.GetVariable("Cfg_MaximumItemsPerPacket").Value;
    }

    private int GetMaximumPublishTime()
    {
        return LogicObject.GetVariable("Cfg_MaximumPublishTime").Value;
    }

    private int GetMinimumPublishTime()
    {
        return LogicObject.GetVariable("Cfg_MinimumPublishTime").Value;
    }

    private DataLogger GetDataLogger()
    {
        var dataLoggerNodeId = LogicObject.GetVariable("Cfg_DataLogger").Value;
        return InformationModel.Get<DataLogger>(dataLoggerNodeId);
    }

    private string ResourceUriValueToAbsoluteFilePath(UAValue value)
    {
        var resourceUri = new ResourceUri(value);
        return resourceUri.Uri;
    }

    private string GetDataLoggerTableName()
    {
        if (pushAgentConfigurationParameters.dataLogger.TableName != null)
            return pushAgentConfigurationParameters.dataLogger.TableName;

        return pushAgentConfigurationParameters.dataLogger.BrowseName;
    }

    private void CreatePushAgentStore(string browsename, string filename)
    {
        Log.Verbose1("PushAgent", $"Create push agent store with filename: {filename}.");
        try
        {
            SQLiteStore store = InformationModel.MakeObject<SQLiteStore>(browsename);
            store.Filename = filename;
            LogicObject.Add(store);
        }
        catch (Exception e)
        {
            Log.Error("PushAgent", $"Unable to create push agent store ({e.Message}).");
            throw;
        }
    }

    // // Added Fiix ARP support July 2024, Start data summary cycle (default to 1 hour)
    private void StartSummaryCycle()
    {
        if (!isARPenabled) return;
        ARP_PeriodicTask = new PeriodicTask(ARPSummaryTask, arpDataSendInterval * 1000, LogicObject);
        ARP_PeriodicTask.Start();
    }

    [ExportMethod]
    public void ARPSummaryTask()
    {
        DateTime endTime = DateTime.UtcNow;
        DateTime startTime = DateTime.UtcNow.AddSeconds(-arpDataSendInterval);
        Folder assetsFolder = LogicObject.Owner.Owner.Find<Folder>("Assets");

        // Get array of ARP Summmary data object, each member is for one sensor.
        ArpDataMessage[] arpDataMessage = ARPDataCalculation(startTime, endTime, "");

        // Prepare an full asset list for searching
        List<Asset> fullAssetList = assetsFolder.FindNodesByType<Asset>().ToList();
        
        // Convert ARPData to JSON string, update ARP object in Fiix Asset model if arpData array length is not zero
        foreach (var dataMessage in arpDataMessage)
        {
            if (dataMessage.arpData.Length>0)
            {
                // Get related asset id, asset, EU, extract ARP Data name from EU part
                int assetIDPos = dataMessage.sensorId.IndexOf("_AssetID");
                int EUPos = dataMessage.sensorId.IndexOf("_EU");
                int EUIDPos = dataMessage.sensorId.IndexOf("_EUID");

                if (assetIDPos < 0 || EUPos < 0) { Log.Error("ARP Summary Task", "process " + dataMessage.sensorId + " with error."); continue; };
                string assetName = dataMessage.sensorId.Substring(0,assetIDPos);
                assetIDPos = assetIDPos + 8;
                string assetID = dataMessage.sensorId.Substring(assetIDPos, EUPos - assetIDPos);
                EUPos = EUPos + 3;
                string arpEU = dataMessage.sensorId.Substring(EUPos, EUIDPos - EUPos);
                int EUDelimitPos = arpEU.IndexOf("__");
                string arpName = arpEU.Substring(EUDelimitPos+2);

                IUANode arpAsset = fullAssetList.Find(asset => asset.BrowseName == assetName && (int)asset.GetVariable("id").Value == int.Parse(assetID));

                if (arpAsset== null || arpAsset.FindNodesByType<ARPData>() == null ) 
                {
                    Log.Error("ARP Summary Task", "process " + dataMessage.sensorId + " with no ARP Data found."); continue; 
                }
                // Find corresponding ARPData by browse name which embeded in variable name EU, instead of using EU like MeterReading
                // ARPData arpData = arpAsset.FindNodesByType<ARPData>().ToList().Find(arp => arp.dataReadingVariable.EngineeringUnits.DisplayName.Text == arpEU);
                ARPData arpData = arpAsset.FindNodesByType<ARPData>().ToList().Find(arp => arp.BrowseName == arpName);

                if (arpData != null)
                {
                    JsonSerializerSettings dateFormatSettings = new JsonSerializerSettings
                    {
                        DateFormatString = "yyyy-MM-ddTHH:mm:ss.fffZ"
                    };
           // FORMATTING Sensor ID for ARP: Update DataMessage sensorID with [AssetName]_[AssetID]_[ARPDataName] as sensorId
                    dataMessage.sensorId = assetName + "_AssetID" + assetID + "_" + arpName;

                    arpData.Out_JSON = JsonConvert.SerializeObject(dataMessage, dateFormatSettings);
                    Log.Info("Fiix ARP function", "Generated payload for " + assetName + "'s " + arpData.BrowseName + " with JSON as " + arpData.Out_JSON);
                } 
            }
        }
    }

    // ARP function to calculate ARP data in MeterReading DataStore for given time period in resolution of ARPCalculationTimePeriodInSecond,
    // filter on varialbe name with given string if it is not empty, read from DataStore into a Temp table once
    // Loop through sensor (analog variable), and slice into 1 min dataset to do calculation, and generate ArpDataPackets
    // return Array of ArpDataMessage objects
    public ArpDataMessage[] ARPDataCalculation(DateTime startTime, DateTime endTime, string filterName)
    {
        // Define context data summary rule, these are used directly in SQL Select statement to aggregate context values. Can change to any supported syntax.
        string ruleMachineRunning = "MAX";
        string ruleRecipe = "MIN";
        string ruleFault = "MIN";
        string ruleMessage = "MAX";

        if (LogicObject.GetVariable("Cfg_ARP_MachineRunning_AggregateRule")!=null && LogicObject.GetVariable("Cfg_ARP_MachineRunning_AggregateRule").Value != "") ruleMachineRunning = ((string)LogicObject.GetVariable("Cfg_ARP_MachineRunning_AggregateRule").Value).Trim();
        if (LogicObject.GetVariable("Cfg_ARP_Recipe_AggregateRule") != null && LogicObject.GetVariable("Cfg_ARP_Recipe_AggregateRule").Value != "") ruleRecipe = ((string)LogicObject.GetVariable("Cfg_ARP_Recipe_AggregateRule").Value).Trim();
        if (LogicObject.GetVariable("Cfg_ARP_Fault_AggregateRule") != null && LogicObject.GetVariable("Cfg_ARP_Fault_AggregateRule").Value != "") ruleFault = ((string)LogicObject.GetVariable("Cfg_ARP_Fault_AggregateRule").Value).Trim();
        if (LogicObject.GetVariable("Cfg_ARP_Message_AggregateRule") != null && LogicObject.GetVariable("Cfg_ARP_Message_AggregateRule").Value != "") ruleMessage = ((string)LogicObject.GetVariable("Cfg_ARP_Message_AggregateRule").Value).Trim();

        //  Scan DataStore for the provided time period 's ARP data
        Log.Info("FiixGatewayRuntime", "Running ARP summary calculation at " + DateTime.Now.ToString() + ".");
        List<ArpDataMessage> arpDataMessageList = new List<ArpDataMessage>();

        string startTimeStr = startTime.ToString("s", System.Globalization.CultureInfo.InvariantCulture);
        string endTimeStr = endTime.ToString("s", System.Globalization.CultureInfo.InvariantCulture);
        Store fiixStore = InformationModel.Get<FTOptix.Store.Store>(pushAgentConfigurationParameters.dataLogger.Store);
        DataLogger fiixLogger = pushAgentConfigurationParameters.dataLogger;
        List<VariableToLog> arpVariablesToLogList;
        if (filterName.Trim()=="") arpVariablesToLogList = fiixLogger.VariablesToLog.ToList().FindAll(variable => variable.BrowseName.Contains("FiixARP") && !variable.BrowseName.Contains("FiixARP1_JSON"));
        else arpVariablesToLogList = fiixLogger.VariablesToLog.ToList().FindAll(variable => variable.BrowseName.Contains("FiixARP") && !variable.BrowseName.Contains("FiixARP1_JSON") && variable.BrowseName.Contains(filterName));

        String tableName = GetDataLoggerTableName();
        String ARPQueryColumns = String.Empty;
        String[] ARPVariableNames;

        // Get variable and column names
        List<string> columnNames = new List<string>();

        foreach (var variable in arpVariablesToLogList)
        {
            // add all ARP data to columns string
            if (ARPQueryColumns != string.Empty) ARPQueryColumns += ", ";
            ARPQueryColumns += "\"" + variable.BrowseName + "\"";

            // Add the main variables to list
            if (!(variable.BrowseName.Contains("FiixARP0_") || variable.BrowseName.Contains("FiixARP1_"))) columnNames.Add(variable.BrowseName);
        }
        ARPVariableNames = columnNames.ToArray();

        // Copy ARP Data of the time period into temp table
        try
        {
            string query =  $"CREATE TEMPORARY TABLE \"##tempARPDataTable\" AS " +
                            $"SELECT {ARPQueryColumns} " + ",\"Timestamp\" " +
                            $"FROM \"{tableName}\" " +
                            $"WHERE \"Timestamp\" >= \"{startTimeStr}\" AND \"Timestamp\" < \"{endTimeStr}\" " +
                             $"ORDER BY \"Timestamp\" ASC ";

            if (fiixStore.Status == StoreStatus.Online)
                fiixStore.Query(query, out _, out _);           
        }
        catch (Exception e)
        {
            Log.Error("Fiix Gateway ARP function", $"Failed to read ARP data from store to temporary table: {e.Message}.");
            goto CalculationEnd;
        }

     // Loop through each ARP Variable to do calculation
        foreach (string arpVariableName in ARPVariableNames)
        {
            object[,] resultSet, resultSet2;
            string[] header, header2;

            // Slice into calculation time slots, calculate statistics and ARP context data for each slot.
            // Start from closest 0 second

            DateTime slotStartTime, slotEndTime;
            if (startTime.Second > 0) slotStartTime = startTime.AddSeconds(60 - startTime.Second);
            else slotStartTime = startTime;
            slotEndTime = slotStartTime.AddSeconds(ARPCalculationTimePeriodInSecond);
            string arpVariableName0 = arpVariableName.Remove(arpVariableName.Length - 1) + "0";
            var records = new List<ArpDataPacket>();

            while (slotStartTime < endTime)
            {
                string slotStartTimeStr = slotStartTime.ToString("s", System.Globalization.CultureInfo.InvariantCulture);
                string slotEndTimeStr = slotEndTime.ToString("s", System.Globalization.CultureInfo.InvariantCulture);
                List<double> rawData = new List<double>();

                try
                {
                    // Read summary result, because SQLite doesn't support First and Last, using JOIN the get the first or last value of context data if configured.
                    string query = $"SELECT AVG(ARPDATA.\"{arpVariableName}\") ,MIN(ARPDATA.\"{arpVariableName}\") ,MAX(ARPDATA.\"{arpVariableName}\") ,COUNT(ARPDATA.\"{arpVariableName}\")";
                    query += (ruleMachineRunning == "FIRST" || ruleMachineRunning == "LAST")? $", Running.\"{arpVariableName0}_bolIsMachineRunning\"": $", { ruleMachineRunning} (\"{arpVariableName0}_bolIsMachineRunning\")";
                    query += (ruleRecipe == "FIRST" || ruleRecipe == "LAST") ? $", Recipe.\"{arpVariableName0}_strRecipe\"" : $", {ruleRecipe} (\"{arpVariableName0}_strRecipe\")";
                    query += (ruleFault == "FIRST" || ruleFault == "LAST") ? $", Fault.\"{arpVariableName0}_strFault\"" : $", {ruleFault} (\"{arpVariableName0}_strFault\")";
                    query += (ruleMessage == "FIRST" || ruleMessage == "LAST") ? $", Message.\"{arpVariableName0}_strMessage\"" : $", {ruleMessage} (\"{arpVariableName0}_strMessage\")";
                    query += $", MAX(ARPDATA.\"Timestamp\") FROM \"##tempARPDataTable\" AS ARPDATA ";

                    if (ruleMachineRunning == "FIRST") query += $"INNER JOIN (SELECT \"{arpVariableName0}_bolIsMachineRunning\" FROM \"##tempARPDataTable\" ORDER BY \"Timestamp\" ASC LIMIT 1) AS Running ON 1=1 ";
                    if (ruleMachineRunning == "LAST") query += $"INNER JOIN (SELECT \"{arpVariableName0}_bolIsMachineRunning\" FROM \"##tempARPDataTable\" ORDER BY \"Timestamp\" DESC LIMIT 1) AS Running ON 1=1 ";
                    if (ruleRecipe == "FIRST") query += $"INNER JOIN (SELECT \"{arpVariableName0}_strRecipe\" FROM \"##tempARPDataTable\" ORDER BY \"Timestamp\" ASC LIMIT 1) AS Recipe ON 1=1 ";
                    if (ruleRecipe == "LAST") query += $"INNER JOIN (SELECT \"{arpVariableName0}_strRecipe\" FROM \"##tempARPDataTable\" ORDER BY \"Timestamp\" DESC LIMIT 1) AS Recipe ON 1=1 ";
                    if (ruleFault == "FIRST") query += $"INNER JOIN (SELECT \"{arpVariableName0}_strFault\" FROM \"##tempARPDataTable\" ORDER BY \"Timestamp\" ASC LIMIT 1) AS Fault ON 1=1 ";
                    if (ruleFault == "LAST") query += $"INNER JOIN (SELECT \"{arpVariableName0}_strFault\" FROM \"##tempARPDataTable\" ORDER BY \"Timestamp\" DESC LIMIT 1) AS Fault ON 1=1 ";
                    if (ruleMessage == "FIRST") query += $"INNER JOIN (SELECT \"{arpVariableName0}_strMessage\" FROM \"##tempARPDataTable\" ORDER BY \"Timestamp\" ASC LIMIT 1) AS Message ON 1=1 ";
                    if (ruleMessage == "LAST") query += $"INNER JOIN (SELECT \"{arpVariableName0}_strMessage\" FROM \"##tempARPDataTable\" ORDER BY \"Timestamp\" DESC LIMIT 1) AS Message ON 1=1 ";

                    query += $"WHERE \"Timestamp\" >= \"{slotStartTimeStr}\" AND \"Timestamp\" < \"{slotEndTimeStr}\" AND \"{arpVariableName0}_enabled\" = 1  " +
                             $"ORDER BY \"Timestamp\" ASC ";
                    fiixStore.Query(query, out header, out resultSet);
                    // Check if the resultSet is a bidimensional array
                    if (resultSet.Rank != 2) throw new Exception ("Get calculation from Temp table, not return bidimensional resultset") ;
                    var rowCount = resultSet != null ? resultSet.GetLength(0) : 0;
                    var columnCount = header != null ? header.Length : 0;
                    if (rowCount == 0 || resultSet[0, 0] == null) goto TimeSlotEnd;

                    // Read raw data to calculate standard deviation
                    string query2 = $"SELECT \"{arpVariableName}\" " +
                                    $"FROM \"##tempARPDataTable\" "  +
                                    $"WHERE \"Timestamp\" >= \"{slotStartTimeStr}\" AND \"Timestamp\" < \"{slotEndTimeStr}\" AND \"{arpVariableName0}_enabled\" = 1  " +
                                    $"ORDER BY \"Timestamp\" ASC ";
                    fiixStore.Query(query2, out header2, out resultSet2);
                    // Check if the resultSet is a bidimensional array
                    if (resultSet2.Rank != 2) throw new Exception("Get raw data from Temp table, not return bidimensional resultset");
                    var rowCount2 = resultSet2 != null ? resultSet2.GetLength(0) : 0;
                    var columnCount2 = header2 != null ? header2.Length : 0;
                    if (rowCount2 == 0 || columnCount2 == 0) goto TimeSlotEnd;
                    for (int k=0; k<rowCount2; k++) 
                    {
                        double rowDouble;
                        if (double.TryParse(resultSet2[k, 0].ToString(), out rowDouble)) rawData.Add(rowDouble);
                    }

                    // create ARP Data object,  add to list
                    ArpDataPacket record = new ArpDataPacket();
                    record.avgValue = Math.Round(double.Parse(resultSet[0, 0].ToString()),5);
                    record.minValue = Math.Round(double.Parse(resultSet[0, 1].ToString()),5);
                    record.maxValue = Math.Round(double.Parse(resultSet[0, 2].ToString()),5);
                    int totalRowCount = int.Parse(resultSet[0, 3].ToString());
                    bool isRunning;
                    record.isMachineRunning = bool.TryParse(resultSet[0, 4].ToString(), out isRunning) ? isRunning: false;
                    record.recipe = resultSet[0, 5]?.ToString();
                    record.fault = resultSet[0, 6]?.ToString();
                    record.message = resultSet[0, 7]?.ToString();
                    record.eventTime = slotEndTime;
                    //record.eventTime = DataLoggerRecordUtils.GetTimestamp(resultSet[0, 8]);

                    // Calculate standard deviation manully
                    double sumOfSquaresOfDifferences = rawData.Select(val => (val - record.avgValue) * (val - record.avgValue)).Sum();
                    record.stddevValue = Math.Round(Math.Sqrt(sumOfSquaresOfDifferences / (rawData.Count -1)),5);

                    if (!(Double.IsNaN(record.stddevValue)|| Double.IsNaN(record.avgValue) || Double.IsNaN(record.minValue) || Double.IsNaN(record.maxValue) )) records.Add(record);
                    //Log.Info("Fiix ARP function", "Create ARP 1 minute data record for " + arpVariableName + " on end time " + slotEndTime);
                    
                }
                catch (Exception e)
                {
                    Log.Error("FiixGatewayRuntime", $"Failed to run ARP calculation task: {e.Message}.");
                }
                TimeSlotEnd:
                slotStartTime = slotStartTime.AddSeconds(ARPCalculationTimePeriodInSecond); ;
                slotEndTime = slotStartTime.AddSeconds(ARPCalculationTimePeriodInSecond);
                if (slotEndTime > endTime) slotEndTime = endTime;
            }
            // Create ArpDataMessage object 
            ArpDataMessage arpDataMessage = new ArpDataMessage();

            arpDataMessage.sensorId = arpVariableName;
            arpDataMessage.arpData = records.ToArray();

            // Add to result list
            arpDataMessageList.Add(arpDataMessage);
        }
        CalculationEnd:
        DeleteARPTemporaryTable(fiixStore);
        return arpDataMessageList.ToArray();
    }
    private void DeleteARPTemporaryTable(Store store)
    {
        object[,] resultSet;
        string[] header;

        try
        {
            string query = $"DROP TABLE \"##tempARPDataTable\"";
            store.Query(query, out header, out resultSet);
        }
        catch (Exception e)
        {
            Log.Error("Fiix ARP function", $"Failed to delete internal temporary table: {e.Message}.");
            throw;
        }
    }


    private readonly object dataFetchLock = new object();
    private bool insertOpCode;
    private bool insertVariableTimestamp;
    private bool logLocalTime;
    private int nextRestartTimeout;
    private Packet pendingSendPacket;
    private Packet pendingARPSendPacket;
    private DelayedTask dataFetchTask;
    private PushAgentConfigurationParameters pushAgentConfigurationParameters;
    //private MQTTConnector mqttClientConnector;
    private SupportStore pushAgentStore;
    private DataLoggerStoreWrapper dataLoggerStore;
    private DataLoggerStatusStoreWrapper statusStoreWrapper;
    private JSONBuilder jsonCreator;
    private CancellationTokenSource cancellationTokenSource;
    DataLoggerRecordPuller dataLoggerRecordPuller;
    private bool enableGatewaySend;
    int recordToSendCount;
    private DelayedTask ARPDelayedTask;
    private PeriodicTask ARP_PeriodicTask;
    private bool isARPenabled = false;
    private int arpDataSendInterval;
    internal CmmsHttpClientHandler cmmsHttpHandlerSingleton;
    internal ArpHttpClientHandler arpHttpHandlerSingleton;


    class MQTTConfigurationParameters
    {
        public string clientId;
        public string brokerIPAddress;
        public int brokerPort;
        public string brokerTopic;
        public int qos;
        public bool useSSL;
        public string pathClientCert;
        public string passwordClientCert;
        public string pathCACert;
        public string username;
        public string password;
    }

    class PushAgentConfigurationParameters
    {
        public MQTTConfigurationParameters mqttConfigurationParameters;
        public DataLogger dataLogger;
        public bool pushFullSample;
        public bool preserveDataLoggerHistory;
    }
}

namespace HttpAPIGateway
{
    public static class GatewayUtils
    {
        private static CmmsApiConnectionConfiguration cmms_APIConnectionConfiguration = GetFiix_APIConnectionConfiguration();
        private static ArpApiConnectionConfiguration arp_APIConnectionConfiguration = GetARP_APIConnectionConfiguration();

        private static readonly CmmsHttpClientHandler cmmsHttpClientSingletonHandler = CmmsHttpClientHandler.Create(cmms_APIConnectionConfiguration, true);
        private static readonly ArpHttpClientHandler arpHttpClientSingletonHandler = ArpHttpClientHandler.Create(arp_APIConnectionConfiguration, true);

        public static CmmsApiConnectionConfiguration GetFiix_APIConnectionConfiguration()
        {
            CmmsApiConnectionConfiguration config = new CmmsApiConnectionConfiguration();
            // Get Configurations from DesigntimeLogic   
            IUANode designtimeLogic = Project.Current.Find("FiixGatewayDesigntimeLogic");
            if (designtimeLogic == null)
            {
                Log.Error("Fiix Gateway", "Get Fiix Http Client error: Couldnot find DesignTimeLogic to get configuration");
                return null;
            }
            config.Url = designtimeLogic.GetVariable("Cfg_FiixURL").Value; 
            config.SecretKey = designtimeLogic.GetVariable("Cfg_SecretKey").Value;
            config.AccessKey = designtimeLogic.GetVariable("Cfg_AccessKey").Value;
            config.AppKey = designtimeLogic.GetVariable("Cfg_AppKey").Value;
            return config;
        }

        public static ArpApiConnectionConfiguration GetARP_APIConnectionConfiguration()
        {
            ArpApiConnectionConfiguration config = new ArpApiConnectionConfiguration();
            // Get Configurations from DesigntimeLogic   
            IUANode designtimeLogic = Project.Current.Find("FiixGatewayDesigntimeLogic");
            if (designtimeLogic == null)
            {
                Log.Error("Fiix Gateway", "Get Fiix Http Client error: Couldnot find DesignTimeLogic to get configuration");
                return null;
            }
            config.ApiKey = designtimeLogic.GetVariable("Cfg_ARP_ApiKey").Value;
            config.ArpURL = designtimeLogic.GetVariable("Cfg_ARP_URL").Value;
            config.ApiSecret = designtimeLogic.GetVariable("Cfg_ARP_ApiSecret").Value;
            return config;
        }

        public static CmmsApiService GetFiixCMMS_SingletonAPIService()
        {
            return new CmmsApiService(cmmsHttpClientSingletonHandler);
        }

        public static void DisposeFiixCMMS_SingletonAPIService()
        {
            if (cmmsHttpClientSingletonHandler != null) cmmsHttpClientSingletonHandler.Dispose();
        }

        public static ArpApiService GetFiixARP_SingletonAPIService()
        {
            return new ArpApiService(arpHttpClientSingletonHandler);
        }

        public static void DisposeFiixARP_SingletonAPIService()
        {
            if (arpHttpClientSingletonHandler != null) arpHttpClientSingletonHandler.Dispose();
        }

        public static void SyncAssetTree(bool isDesignTimeRun)
        {
            // when run in Runtime mode, no node will be added or removed upon discrepancy from call result.
            
            IUANode LogicObject = Project.Current.Find("FiixGatewayDesigntimeLogic");
            IUANode reportObject = LogicObject;
            if (!isDesignTimeRun) { reportObject = Project.Current.Find("FiixGatewayRuntimeLogic"); }
            if (LogicObject == null)
            {
                Log.Error("Fiix Gateway", "Update Assets in Runtime error: Couldnot find DesignTimeLogic to get configuration");
                return;
            }

            // Added Sep 2024 for v1.2 Work Order function
            List<Fiix_WorkOrder> openWorkOrderList = new List<Fiix_WorkOrder>();
            IUANode woStatusFolder = Project.Current.Find<Folder>("WorkOrderStatus");
            List<IUANode> statusList = woStatusFolder.Children.Cast<IUANode>().ToList();
            
            // Find all Work Order Status whose ControlID is not 102 (designated by Fiix API for Closed)
            List<IUANode> nonClosedWOStatusList = statusList.FindAll(x => (int)x.GetVariable("intControlID").Value!=102);

            IUANode modelFolder = LogicObject.Owner.Owner.Find("Assets");
            IUANode AssetType = LogicObject.Owner.Find("Asset");
            CmmsApiService apiService = GatewayUtils.GetFiixCMMS_SingletonAPIService();

            int newSiteCount = 0, updateSiteCount = 0, newAssetCount = 0, updateAssetCount = 0;

            // Sync Sites
            string filterName = (string)LogicObject.GetVariable("Set_FilterSiteNames").Value;
            ApiResponseModel responseModel = apiService.FindAssetsBatch(true, -1, "", -1, filterName, false).Result;
            Fiix_Asset[] fiixSites;
            reportObject.GetVariable("Sts_LastExecutionDatetime").Value = DateTime.Now;
            if (!responseModel.Success)
            {
                reportObject.GetVariable("Sts_LastExecutionResult").Value = "Get Fiix Sites with no result.";
                if (!responseModel.Success) Log.Error("Fiix Gateway", "Sync Asset Tree error: " + responseModel.ErrorMessage);
                return;
            }
            fiixSites = responseModel.objects.OfType<Fiix_Asset>().ToArray();
            List<IUANode> sites = modelFolder.Children.Cast<IUANode>().ToList();

            // Delete extra nodes if enabled
            if (isDesignTimeRun && (bool)LogicObject.GetVariable("Set_DeleteExtraNodes").Value)
            {
                foreach (IUANode site in sites)
                {
                    if (!Array.Exists(fiixSites, fiixsite => fiixsite.id == (int)site.GetVariable("id").Value || fiixsite.strName == (string)site.GetVariable("strName").Value))
                    {
                        site.Delete();
                    }
                }
            }

            foreach (var fiixsite in fiixSites)
            {
                IUANode newSite;

                if (!sites.Exists(site => fiixsite.id == (int)site.GetVariable("id").Value))
                {
                    newSite = InformationModel.MakeObject("Site_" + fiixsite.strName, AssetType.NodeId);
                    newSite.GetVariable("id").Value = fiixsite.id;
                    newSite.GetVariable("strName").Value = fiixsite.strName;
                    newSite.GetVariable("strCode").Value = fiixsite.strCode;
                    newSite.GetVariable("strAddressParsed").Value = fiixsite.strAddressParsed;
                    newSite.GetVariable("strTimezone").Value = fiixsite.strTimezone;
                    newSite.GetVariable("intAssetLocationID").Value = fiixsite.intAssetLocationID;
                    newSite.GetVariable("intCategoryID").Value = fiixsite.intCategoryID;
                    newSite.GetVariable("intSiteID").Value = fiixsite.intSiteID;
                    newSite.GetVariable("intSuperCategorySysCode").Value = fiixsite.intSuperCategorySysCode;
                    newSite.GetVariable("strBinNumber").Value = fiixsite.strBinNumber;
                    newSite.GetVariable("strRow").Value = fiixsite.strRow;
                    newSite.GetVariable("strAisle").Value = fiixsite.strAisle;
                    newSite.GetVariable("strDescription").Value = fiixsite.strDescription;
                    newSite.GetVariable("strInventoryCode").Value = fiixsite.strInventoryCode;
                    newSite.GetVariable("strMake").Value = fiixsite.strMake;
                    newSite.GetVariable("strModel").Value = fiixsite.strModel;
                    newSite.GetVariable("strSerialNumber").Value = fiixsite.strSerialNumber;
                    newSite.GetVariable("bolIsOnline").Value = Convert.ToBoolean(fiixsite.bolIsOnline);
                    newSite.GetVariable("bolIsSite").Value = Convert.ToBoolean(fiixsite.bolIsSite);
                    newSite.GetVariable("bolIsRegion").Value = Convert.ToBoolean(fiixsite.bolIsRegion);
                    newSite.GetVariable("dtUpdated").Value = DateTimeOffset.FromUnixTimeMilliseconds(fiixsite.intUpdated).DateTime;

                    newSiteCount++;
                    modelFolder.Add(newSite);

                    // Sort by name
                    var updatedSites = modelFolder.Children.Cast<IUANode>().ToList();
                    var cpCount = sites.Count();
                    for (int i = cpCount - 1; i >= 0; i--)
                    {
                        try
                        {
                            if (string.Compare(updatedSites[i].BrowseName, newSite.BrowseName) > 0 || updatedSites[i].BrowseName == "Template") newSite.MoveUp();
                        }
                        catch { Log.Info("Fiix Gateway", "error when sorting sites"); }
                    }
                }
                else
                {
                    newSite = sites.Find(site => fiixsite.id == site.GetVariable("id").Value);
                    newSite.BrowseName = "Site_" + fiixsite.strName;
                    newSite.GetVariable("strName").Value = fiixsite.strName;
                    newSite.GetVariable("strCode").Value = fiixsite.strCode;
                    newSite.GetVariable("strAddressParsed").Value = fiixsite.strAddressParsed;
                    newSite.GetVariable("strTimezone").Value = fiixsite.strTimezone;
                    newSite.GetVariable("intAssetLocationID").Value = fiixsite.intAssetLocationID;
                    newSite.GetVariable("intCategoryID").Value = fiixsite.intCategoryID;
                    newSite.GetVariable("intSiteID").Value = fiixsite.intSiteID;
                    newSite.GetVariable("intSuperCategorySysCode").Value = fiixsite.intSuperCategorySysCode;
                    newSite.GetVariable("strBinNumber").Value = fiixsite.strBinNumber;
                    newSite.GetVariable("strRow").Value = fiixsite.strRow;
                    newSite.GetVariable("strAisle").Value = fiixsite.strAisle;
                    newSite.GetVariable("strDescription").Value = fiixsite.strDescription;
                    newSite.GetVariable("strInventoryCode").Value = fiixsite.strInventoryCode;
                    newSite.GetVariable("strMake").Value = fiixsite.strMake;
                    newSite.GetVariable("strModel").Value = fiixsite.strModel;
                    newSite.GetVariable("strSerialNumber").Value = fiixsite.strSerialNumber;
                    newSite.GetVariable("bolIsOnline").Value = Convert.ToBoolean(fiixsite.bolIsOnline);
                    newSite.GetVariable("bolIsSite").Value = Convert.ToBoolean(fiixsite.bolIsSite);
                    newSite.GetVariable("bolIsRegion").Value = Convert.ToBoolean(fiixsite.bolIsRegion);
                    newSite.GetVariable("dtUpdated").Value = DateTimeOffset.FromUnixTimeMilliseconds(fiixsite.intUpdated).DateTime;
                    updateSiteCount++;
                }
                // Added Sep 2024 for v1.2 workorder function. Scan each site for open work orders.
                if (nonClosedWOStatusList.Count == 0) Log.Error("Fiix Gateway", "Get open work orders for site " + fiixsite.strName + " error: Could not get Non_Closed status list.");
                else
                {
                    foreach (IUANode wos in nonClosedWOStatusList)
                    {
                        if (wos == null || wos.Children == null ) continue;
                        ApiResponseModel responseModelWO = apiService.FindWorkOrderByStatus(wos.GetVariable("id").Value, fiixsite.id).Result;
                        if (responseModelWO.Success)
                        {
                            foreach (Fiix_WorkOrder wo in responseModelWO.objects)
                            {
                                openWorkOrderList.Add(wo);
                            }
                        }
                        else
                        {
                            Log.Error("Fiix Gateway", "Get work orders with status " + wos.BrowseName + " for site " + fiixsite.strName + " error: " + responseModelWO.ErrorMessage);
                        }
                    }
                }
            }

            // Sync all Assets with isSite is false
            sites = modelFolder.Children.Cast<IUANode>().ToList();
            //if (modelFolder.Find("Template") != null) sites.Remove(modelFolder.Get("Template"));

            string sfilterName = (string)LogicObject.GetVariable("Set_FilterAssetNames").Value;
            Fiix_Asset[] fiixAllAssets;

            // Get enabled CategoryID array from Model Folder
            string part1 = "", part2 = "", strCategoryIDFilter = "";
            try
            {
                if ((bool)LogicObject.GetVariable("Set_FilterEnabledAssetCategoryOnly").Value)
                {
                    var categoryFolder = LogicObject.Owner.Owner.Find("AssetCategories");
                    if (categoryFolder != null)
                    {
                        List<IUANode> assetCategories = categoryFolder.Children.Cast<IUANode>().ToList();
                        foreach (IUANode category in assetCategories)
                        {
                            if ((bool)category.GetVariable("Cfg_enabled").Value)
                            {
                                part1 += "?,";
                                part2 += category.GetVariable("id").Value + ",";
                            }
                        }
                        if (part1.Length > 0) part1 = part1.Remove(part1.Length - 1, 1);
                        if (part2.Length > 0) part2 = part2.Remove(part2.Length - 1, 1);
                    }
                }
            }
            catch (Exception ex)
            { Log.Error("Fiix Gateway", "Prepare Asset Filter by Category error: " + ex.Message); }
            if (part1 != "") strCategoryIDFilter = "{ \"ql\": \"intCategoryID IN (" + part1 + " )\", \"parameters\" : [" + part2 + "]}";

            // Get all assets if filter string is empty, otherwise get all Location/Facility and assets seperately with filter on name.
            if (sfilterName != null && sfilterName.Trim() == "" && !(bool)LogicObject.GetVariable("Set_FilterEnabledAssetCategoryOnly").Value)
            {
                fiixAllAssets = apiService.FindAssetsBatch(false, -1, "", -1, sfilterName, false).Result.objects.OfType<Fiix_Asset>().ToArray();
            }
            else
            {
                // Seperate Facility/Location with Equipment/Tool assets for filtering by name function. Get Equipment/Tool with text included in filter only.
                Fiix_Asset[] pureAssets = apiService.FindAssetsBatch(false, 1, strCategoryIDFilter, -1, sfilterName, false).Result.objects?.OfType<Fiix_Asset>().ToArray();
                Fiix_Asset[] nonAssets = apiService.FindAssetsBatch(false, 2, "", -1, "", false).Result.objects?.OfType<Fiix_Asset>().ToArray();
                if (nonAssets == null || nonAssets.Length == 0)
                {
                    if (pureAssets != null) fiixAllAssets = pureAssets.ToArray();
                    else fiixAllAssets = null;
                }
                else
                {
                    if (pureAssets != null && pureAssets.Length > 0) fiixAllAssets = nonAssets.Concat(pureAssets).ToArray();
                    else fiixAllAssets = nonAssets;
                }
            }

            if (fiixAllAssets == null || fiixAllAssets.Length == 0)
            {
                reportObject.GetVariable("Sts_LastExecutionResult").Value += ", Get Fiix Facilities with no result.";
                return;
            }

            // Loop through sites to nested call find fiixFacility with parentID
            // if found in fiix array, check it is in Nodes (under the site), update when yes, create when no; remove the node from Nodes list
            // Check Delete extra node flag, remove nodes left in Nodes list
            foreach (IUANode site in sites) AddUpdateFacilityByLocation(site, fiixAllAssets);

            reportObject.GetVariable("Sts_LastExecutionResult").Value = newSiteCount + " new and " + updateSiteCount + " synced sites; " + newAssetCount + " new and " + updateAssetCount + " synced assets";

            void AddUpdateFacilityByLocation(IUANode parentNode, Fiix_Asset[] assets)
            {
                if (parentNode == null) return;
                // Get existing object nodes children
                var existingChildren = parentNode.Children.Cast<IUANode>().ToList();
                existingChildren.RemoveAll(x => x.NodeClass != NodeClass.Object || x.BrowseName.Contains("DesignTimeLogic"));

                foreach (Fiix_Asset asset in assets)
                {
                    // Check parent child relationship. Specially for (No Site), link any asset with AssetLocationID is 0 to (No Site)'s ID
                    bool isRootLocationWithoutSite = ((string)parentNode.GetVariable("strName").Value).Contains("(No Site)") && asset.intAssetLocationID == 0;
                    if ((asset.intAssetLocationID == (int)parentNode.GetVariable("id").Value) || (asset.intAssetParentID == (int)parentNode.GetVariable("id").Value) || isRootLocationWithoutSite)
                    {
                        // Check if the child already existing by id
                        IUANode currentNode = null;
                        bool found = false;

                        foreach (IUANode childNode in existingChildren)
                        {
                            if ((int)childNode.GetVariable("id").Value == asset.id)
                            // node with the same id exist, update; Specially for (No Site), update its children's LocationID from 0 to (No Site)'s ID
                            {
                                if (isRootLocationWithoutSite) childNode.GetVariable("intAssetLocationID").Value = (int)parentNode.GetVariable("id").Value;
                                else   // Updated in V1.01 to cover either AssetLocationID or AssetParentID 
                                {
                                    if (asset.intAssetLocationID != 0)
                                    {
                                        childNode.GetVariable("intAssetLocationID").Value = asset.intAssetLocationID;
                                        if (asset.intAssetParentID == 0) childNode.GetVariable("intAssetParentID").Value = asset.intAssetLocationID;
                                        else childNode.GetVariable("intAssetParentID").Value = asset.intAssetParentID;
                                    }
                                    else
                                    {
                                        childNode.GetVariable("intAssetLocationID").Value = asset.intAssetParentID;
                                        childNode.GetVariable("intAssetParentID").Value = asset.intAssetParentID;
                                    }
                                }
                                childNode.GetVariable("intCategoryID").Value = asset.intCategoryID;
                                childNode.GetVariable("intSiteID").Value = asset.intSiteID;
                                childNode.GetVariable("intSuperCategorySysCode").Value = asset.intSuperCategorySysCode;
                                childNode.GetVariable("strAddressParsed").Value = asset.strAddressParsed;
                                childNode.GetVariable("strCode").Value = asset.strCode;
                                childNode.GetVariable("strName").Value = asset.strName;
                                childNode.GetVariable("strTimezone").Value = asset.strTimezone;
                                childNode.GetVariable("strBinNumber").Value = asset.strBinNumber;
                                childNode.GetVariable("strRow").Value = asset.strRow;
                                childNode.GetVariable("strAisle").Value = asset.strAisle;
                                childNode.GetVariable("strDescription").Value = asset.strDescription;
                                childNode.GetVariable("strInventoryCode").Value = asset.strInventoryCode;
                                childNode.GetVariable("strMake").Value = asset.strMake;
                                childNode.GetVariable("strModel").Value = asset.strModel;
                                childNode.GetVariable("strSerialNumber").Value = asset.strSerialNumber;
                                childNode.GetVariable("bolIsOnline").Value = Convert.ToBoolean(asset.bolIsOnline);
                                childNode.GetVariable("bolIsSite").Value = Convert.ToBoolean(asset.bolIsSite);
                                childNode.GetVariable("bolIsRegion").Value = Convert.ToBoolean(asset.bolIsRegion);
                                childNode.GetVariable("dtUpdated").Value = DateTimeOffset.FromUnixTimeMilliseconds(asset.intUpdated).DateTime;
                                //Log.Info("Testing", "count of " + asset.strName + " is " + openWorkOrderList.Count(wo => wo.strAssetIds.Contains(childNode.GetVariable("id").Value)));
                                childNode.GetVariable("Sts_OpenWorkOrderCount").Value = openWorkOrderList.Count(wo => wo.strAssetIds.Contains(childNode.GetVariable("id").Value));
                                updateAssetCount++;
                                existingChildren.Remove(childNode);
                                currentNode = childNode;
                                found = true;
                                break;
                            }
                        };
                        if (!found && isDesignTimeRun)      // add new; Specially for (No Site), update its children's LocationID from 0 to (No Site)'s ID
                        {
                            IUANode newNode = InformationModel.MakeObject(asset.strName, AssetType.NodeId);
                            newNode.GetVariable("id").Value = asset.id;
                            if (isRootLocationWithoutSite) newNode.GetVariable("intAssetLocationID").Value = (int)parentNode.GetVariable("id").Value;
                            else
                            {
                                if (asset.intAssetLocationID != 0)
                                {
                                    newNode.GetVariable("intAssetLocationID").Value = asset.intAssetLocationID;
                                    if (asset.intAssetParentID == 0) newNode.GetVariable("intAssetParentID").Value = asset.intAssetLocationID;
                                    else newNode.GetVariable("intAssetParentID").Value = asset.intAssetParentID;
                                }
                                else
                                {
                                    newNode.GetVariable("intAssetLocationID").Value = asset.intAssetParentID;
                                    newNode.GetVariable("intAssetParentID").Value = asset.intAssetParentID;
                                }
                            }
                            newNode.GetVariable("intCategoryID").Value = asset.intCategoryID;
                            newNode.GetVariable("intSiteID").Value = asset.intSiteID;
                            newNode.GetVariable("intSuperCategorySysCode").Value = asset.intSuperCategorySysCode;
                            newNode.GetVariable("strAddressParsed").Value = asset.strAddressParsed;
                            newNode.GetVariable("strCode").Value = asset.strCode;
                            newNode.GetVariable("strName").Value = asset.strName;
                            newNode.GetVariable("strTimezone").Value = asset.strTimezone;
                            newNode.GetVariable("strBinNumber").Value = asset.strBinNumber;
                            newNode.GetVariable("strRow").Value = asset.strRow;
                            newNode.GetVariable("strAisle").Value = asset.strAisle;
                            newNode.GetVariable("strDescription").Value = asset.strDescription;
                            newNode.GetVariable("strInventoryCode").Value = asset.strInventoryCode;
                            newNode.GetVariable("strMake").Value = asset.strMake;
                            newNode.GetVariable("strModel").Value = asset.strModel;
                            newNode.GetVariable("strSerialNumber").Value = asset.strSerialNumber;
                            newNode.GetVariable("bolIsOnline").Value = Convert.ToBoolean(asset.bolIsOnline);
                            newNode.GetVariable("bolIsSite").Value = Convert.ToBoolean(asset.bolIsSite);
                            newNode.GetVariable("bolIsRegion").Value = Convert.ToBoolean(asset.bolIsRegion);
                            newNode.GetVariable("dtUpdated").Value = DateTimeOffset.FromUnixTimeMilliseconds(asset.intUpdated).DateTime;
                            newNode.GetVariable("Sts_OpenWorkOrderCount").Value = openWorkOrderList.Count(wo => wo.strAssetIds.Contains(newNode.GetVariable("id").Value));
                            parentNode.Add(newNode);
                            currentNode = newNode;
                            newAssetCount++;
                        }
                        AddUpdateFacilityByLocation(currentNode, assets);
                    }
                }
                // Delete extra node based on setting
                if (isDesignTimeRun && (bool)LogicObject.GetVariable("Set_DeleteExtraNodes").Value)
                {
                    foreach (IUANode childNode in existingChildren)
                    {
                        childNode.Delete();
                    }
                }
            }
        }

        // Prepare API payload for Fiix Add meter reading from DataLogger record, one variable per row format.
        // Decode base on var naming:  [AssetName]_AssetID[AssetID]_EU[EUName]_EUID[EUID]
        // Added ARP support July 2024, detect ARP string in ID composition; Skip FiixARP1_JSON data which is to be sent using different http client.
        public static string GetMeterReadingBatchPayloadFromLogRecords(List<VariableRecord> records)
        {
            
            string jsonBatchPayload = " ";

            try
            {
                foreach (VariableRecord record in records)
                {
                    // Skip FiixARP1_JSON data which is not meter reading data.
                    if (record.variableId.Contains("FiixARP1_JSON")) continue;
                    DateTimeOffset dto = new DateTimeOffset((DateTime)record.timestamp, TimeSpan.Zero);
                    Int64 dt = dto.ToUnixTimeMilliseconds();
                    int assetIDPos = record.variableId.IndexOf("_AssetID");
                    int EUPos = record.variableId.IndexOf("_EU");
                    int EUIDPos = record.variableId.IndexOf("_EUID");
                    int ARPPos = record.variableId.IndexOf("_FiixARP");
                    if (assetIDPos < 0 || EUPos < 0 || EUIDPos < 0) continue;

                    assetIDPos = assetIDPos + 8;
                    EUIDPos = EUIDPos + 5;
                    // Log.Info("record:" + record.variableId + "  assetIDPos:" + assetIDPos + " ; EUPos" + EUPos);                
                    string assetID = record.variableId.Substring(assetIDPos, EUPos - assetIDPos);
                    string meterReadingUnitsID = ARPPos == -1 ? record.variableId.Substring(EUIDPos):record.variableId.Substring(EUIDPos,ARPPos - EUIDPos);
                    double meterReading;
                    if (record.value == null) meterReading = Convert.ToDouble(record.serializedValue);
                    else meterReading = (double)record.value;

                    Log.Debug("Sending record with assetID " + assetID + ", unitID " + meterReadingUnitsID + ", value " + meterReading);

                    string jsonPayload = CmmsApiService.GetPayloadBase("AddRequest") + "\"className\": \"MeterReading\",\"fields\":\"id,intUpdated\",";
                    jsonPayload = jsonPayload + "\"object\":{\"dtmDateSubmitted\":" + dt + ",\"dblMeterReading\":" + meterReading + ",\"intAssetID\":" + assetID;
                    jsonPayload = jsonPayload + ",\"intMeterReadingUnitsID\":" + meterReadingUnitsID + ",\"className\":\"MeterReading\"}}";
                    jsonBatchPayload = jsonBatchPayload + jsonPayload + ",";
                }
            }
            catch (Exception e)
            {
                Log.Error("Fiix Gateway", "Error in GetMeterReadingBatchPayloadFromLogRecords" + e.Message);
            }
            jsonBatchPayload = jsonBatchPayload.Remove(jsonBatchPayload.Length - 1, 1);
            return jsonBatchPayload;
        }

        public static string GetEngineeringUnitNameByID(int ID)
        {
            string result = "";
            IUAVariable meterReadingUnits = (IUAVariable)Project.Current.Find("MeterReadingUnits");
            if (meterReadingUnits == null) return result;
            Struct[] euStructItems = (Struct[])meterReadingUnits.Value.Value;
            foreach (EngineeringUnitDictionaryItem item in euStructItems)
            {
                if (item.UnitId == ID )
                {
                    result = item.DisplayName.Text;
                    break;
                }
            }
            return result;
        }


    }

    public abstract class Record
    {
        public Record(DateTime? timestamp)
        {
            this.timestamp = timestamp;
        }

        public override bool Equals(object obj)
        {
            var other = obj as Record;
            return timestamp == other.timestamp;
        }

        public readonly DateTime? timestamp;

    }

    public class DataLoggerRecord : Record
    {
        public DataLoggerRecord(DateTime timestamp, List<VariableRecord> variables) : base(timestamp)
        {
            this.variables = variables;
        }

        public DataLoggerRecord(DateTime timestamp, DateTime? localTimestamp, List<VariableRecord> variables) : base(timestamp)
        {
            this.localTimestamp = localTimestamp;
            this.variables = variables;
        }

        public override bool Equals(object obj)
        {
            DataLoggerRecord other = obj as DataLoggerRecord;

            if (other == null)
                return false;

            if (timestamp != other.timestamp)
                return false;

            if (localTimestamp != other.localTimestamp)
                return false;

            if (variables.Count != other.variables.Count)
                return false;

            for (int i = 0; i < variables.Count; ++i)
            {
                if (!variables[i].Equals(other.variables[i]))
                    return false;
            }

            return true;
        }

        public readonly DateTime? localTimestamp;
        public readonly List<VariableRecord> variables;
    }

    public class VariableRecord : Record
    {
        public VariableRecord(DateTime? timestamp,
                              string variableId,
                              UAValue value,
                              string serializedValue) : base(timestamp)
        {
            this.variableId = variableId;
            this.value = value;
            this.serializedValue = serializedValue;
            this.variableOpCode = null;
        }

        public VariableRecord(DateTime? timestamp,
                              string variableId,
                              UAValue value,
                              string serializedValue,
                              int? variableOpCode) : base(timestamp)
        {
            this.variableId = variableId;
            this.value = value;
            this.serializedValue = serializedValue;
            this.variableOpCode = variableOpCode;
        }

        public override bool Equals(object obj)
        {
            var other = obj as VariableRecord;
            return timestamp == other.timestamp &&
                   variableId == other.variableId &&
                   value == other.value &&
                   serializedValue == other.serializedValue &&
                   variableOpCode == other.variableOpCode;
        }

        public readonly string variableId;
        public readonly string serializedValue;
        public readonly UAValue value;
        public readonly int? variableOpCode;
    }

    public class Packet
    {
        public Packet(DateTime timestamp, string clientId)
        {
            this.timestamp = timestamp.ToUniversalTime();
            this.clientId = clientId;
        }

        public readonly DateTime timestamp;
        public readonly string clientId;
    }

    public class VariablePacket : Packet
    {
        public VariablePacket(DateTime timestamp,
                              string clientId,
                              List<VariableRecord> records) : base(timestamp, clientId)
        {
            this.records = records;
        }

        public readonly List<VariableRecord> records;
    }

    public class DataLoggerRowPacket : Packet
    {
        public DataLoggerRowPacket(DateTime timestamp,
                                   string clientId,
                                   List<DataLoggerRecord> records) : base(timestamp, clientId)
        {
            this.records = records;
        }

        public readonly List<DataLoggerRecord> records;
    }

    public class DataLoggerRecordUtils
    {
        public static List<DataLoggerRecord> GetDataLoggerRecordsFromQueryResult(object[,] resultSet,
                                                                                 string[] header,
                                                                                 List<VariableToLog> variablesToLogList,
                                                                                 bool insertOpCode,
                                                                                 bool insertVariableTimestamp,
                                                                                 bool logLocalTime)
        {
            var records = new List<DataLoggerRecord>();
            var rowCount = resultSet != null ? resultSet.GetLength(0) : 0;
            var columnCount = header != null ? header.Length : 0;
            for (int i = 0; i < rowCount; ++i)
            {
                var j = 0;
                var rowVariables = new List<VariableRecord>();
                DateTime rowTimestamp = GetTimestamp(resultSet[i, j++]);
                DateTime? rowLocalTimestamp = null;
                if (logLocalTime)
                    rowLocalTimestamp = DateTime.Parse(resultSet[i, j++].ToString());

                int variableIndex = 0;
                while (j < columnCount)
                {
                    string variableId = header[j];
                    object value = resultSet[i, j];
                    string serializedValue = SerializeValue(value, variablesToLogList[variableIndex]);

                    DateTime? timestamp = null;
                    if (insertVariableTimestamp)
                    {
                        ++j; // Consume timestamp column
                        var timestampColumnValue = resultSet[i, j];
                        if (timestampColumnValue != null)
                            timestamp = GetTimestamp(timestampColumnValue);
                    }

                    VariableRecord variableRecord;
                    if (insertOpCode)
                    {
                        ++j; // Consume operation code column
                        var opCodeColumnValue = resultSet[i, j];
                        int? opCode = (opCodeColumnValue != null) ? (Int32.Parse(resultSet[i, j].ToString())) : (int?)null;
                        variableRecord = new VariableRecord(timestamp, variableId, GetUAValue(value, variablesToLogList[variableIndex]), serializedValue, opCode);
                    }
                    else
                        variableRecord = new VariableRecord(timestamp, variableId, GetUAValue(value, variablesToLogList[variableIndex]), serializedValue);

                    rowVariables.Add(variableRecord);

                    ++j; // Consume Variable Column
                    ++variableIndex;
                }

                DataLoggerRecord record;
                if (logLocalTime)
                    record = new DataLoggerRecord(rowTimestamp, rowLocalTimestamp, rowVariables);
                else
                    record = new DataLoggerRecord(rowTimestamp, rowVariables);

                records.Add(record);
            }

            return records;
        }

        private static string SerializeValue(object value, VariableToLog variableToLog)
        {
            if (value == null)
                return null;
            var valueType = variableToLog.ActualDataType;
            if (valueType == OpcUa.DataTypes.DateTime)
                return (GetTimestamp(value)).ToString("O");
            else if (valueType == OpcUa.DataTypes.Float)
                return ((float)((double)value)).ToString("G9");
            else if (valueType == OpcUa.DataTypes.Double)
                return ((double)value).ToString("G17");

            return value.ToString();
        }

        private static UAValue GetUAValue(object value, VariableToLog variableToLog)
        {
            if (value == null)
                return null;
            try
            {
                NodeId valueType = variableToLog.ActualDataType;
                if (valueType == OpcUa.DataTypes.Boolean)
                    return new UAValue(Int32.Parse(GetBoolean(value)));
                else if (valueType == OpcUa.DataTypes.Integer)
                    return new UAValue(Int64.Parse(value.ToString()));
                else if (valueType == OpcUa.DataTypes.UInteger)
                    return new UAValue(UInt64.Parse(value.ToString()));
                else if (valueType == OpcUa.DataTypes.Byte)
                    return new UAValue(Byte.Parse(value.ToString()));
                else if (valueType == OpcUa.DataTypes.SByte)
                    return new UAValue(SByte.Parse(value.ToString()));
                else if (valueType == OpcUa.DataTypes.Int16)
                    return new UAValue(Int16.Parse(value.ToString()));
                else if (valueType == OpcUa.DataTypes.UInt16)
                    return new UAValue(UInt16.Parse(value.ToString()));
                else if (valueType == OpcUa.DataTypes.Int32)
                    return new UAValue(Int32.Parse(value.ToString()));
                else if (valueType == OpcUa.DataTypes.UInt32)
                    return new UAValue(UInt32.Parse(value.ToString()));
                else if (valueType == OpcUa.DataTypes.Int64)
                    return new UAValue(Int64.Parse(value.ToString()));
                else if (valueType == OpcUa.DataTypes.UInt64)
                    return new UAValue(UInt64.Parse(value.ToString()));
                else if (valueType == OpcUa.DataTypes.Float)
                    return new UAValue((float)((double)value));
                else if (valueType == OpcUa.DataTypes.Double)
                    return new UAValue((double)value);
                else if (valueType == OpcUa.DataTypes.DateTime)
                    return new UAValue(GetTimestamp(value));
                else if (valueType == OpcUa.DataTypes.String)
                    return new UAValue(value.ToString());
                else if (valueType == OpcUa.DataTypes.ByteString)
                    return new UAValue((ByteString)value);
                else if (valueType == OpcUa.DataTypes.NodeId)
                    return new UAValue((NodeId)value);
            }
            catch (Exception e)
            {
                Log.Warning("DataLoggerRecordUtils", $"Parse Exception: {e.Message}.");
                throw;
            }

            return null;
        }

        private static string GetBoolean(object value)
        {
            var valueString = value.ToString();
            if (valueString == "0" || valueString == "1")
                return valueString;

            if (valueString.ToLower() == "false")
                return "0";
            else
                return "1";
        }

        public static DateTime GetTimestamp(object value)
        {
            if (Type.GetTypeCode(value.GetType()) == TypeCode.DateTime)
                return ((DateTime)value);
            else
                return DateTime.SpecifyKind(DateTime.Parse(value.ToString()), DateTimeKind.Utc);
        }
    }

    public class DataLoggerStoreWrapper
    {
        public DataLoggerStoreWrapper(Store store,
                                      string tableName,
                                      List<VariableToLog> variablesToLogList,
                                      bool insertOpCode,
                                      bool insertVariableTimestamp,
                                      bool logLocalTime)
        {
            this.store = store;
            this.tableName = tableName;
            this.variablesToLogList = variablesToLogList;
            this.insertOpCode = insertOpCode;
            this.insertVariableTimestamp = insertVariableTimestamp;
            this.logLocalTime = logLocalTime;
        }

        public void DeletePulledRecords()
        {
            if (store.Status == StoreStatus.Offline)
                return;

            try
            {
                Log.Verbose1("DataLoggerStoreWrapper", "Delete records pulled from data logger temporary table.");

                string query = $"DELETE FROM \"{tableName}\" AS D " +
                               $"WHERE \"Id\" IN " +
                               $"( SELECT \"Id\" " +
                               $"FROM \"##tempDataLoggerTable\")";

                store.Query(query, out _, out _);
            }
            catch (Exception e)
            {
                Log.Error("DataLoggerStoreWrapper", $"Failed to delete from data logger temporary table {e.Message}.");
                throw;
            }

            DeleteTemporaryTable();
        }

        public List<DataLoggerRecord> QueryNewEntries()
        {
            Log.Verbose1("DataLoggerStoreWrapper", "Query new entries from data logger.");

            if (store.Status == StoreStatus.Offline)
                return new List<DataLoggerRecord>();

            CopyNewEntriesToTemporaryTable();
            List<DataLoggerRecord> records = QueryNewEntriesFromTemporaryTable();

            if (records.Count == 0)
                DeleteTemporaryTable();

            return records;
        }

        public List<DataLoggerRecord> QueryNewEntriesUsingLastQueryId(UInt64 rowId)
        {
            Log.Verbose1("DataLoggerStoreWrapper", $"Query new entries with id greater than {rowId} (store status: {store.Status}).");

            if (store.Status == StoreStatus.Offline)
                return new List<DataLoggerRecord>();

            CopyNewEntriesToTemporaryTableUsingId(rowId);
            List<DataLoggerRecord> records = QueryNewEntriesFromTemporaryTable();

            if (records.Count == 0)
                DeleteTemporaryTable();

            return records;
        }

        public UInt64? GetMaxIdFromTemporaryTable()
        {
            object[,] resultSet;

            try
            {
                string query = $"SELECT MAX(\"Id\") FROM \"##tempDataLoggerTable\"";

                if (store.Status == StoreStatus.Online)
                {
                    store.Query(query, out _, out resultSet);
                    DeleteTemporaryTable();

                    if (resultSet[0, 0] != null)
                    {
                        Log.Verbose1("DataLoggerStoreWrapper", $"Get max id from data logger temporary table returns {resultSet[0, 0]}.");
                        return UInt64.Parse(resultSet[0, 0].ToString());
                    }
                }

                return null;
            }
            catch (Exception e)
            {
                Log.Error("DataLoggerStoreWrapper", $"Failed to query maxid from data logger temporary table: {e.Message}.");
                throw;
            }
        }

        public UInt64? GetDataLoggerMaxId()
        {
            object[,] resultSet;

            try
            {
                string query = $"SELECT MAX(\"Id\") FROM \"{tableName}\"";

                if (store.Status == StoreStatus.Online)
                {
                    store.Query(query, out _, out resultSet);

                    if (resultSet[0, 0] != null)
                    {
                        Log.Verbose1("DataLoggerStoreWrapper", $"Get data logger max id returns {resultSet[0, 0]}.");
                        return UInt64.Parse(resultSet[0, 0].ToString());
                    }
                }

                return null;
            }
            catch (Exception e)
            {
                Log.Error("DataLoggerStoreWrapper", $"Failed to query maxid from data logger temporary table: {e.Message}.");
                throw;
            }
        }

        public StoreStatus GetStoreStatus()
        {
            return store.Status;
        }

        private void CopyNewEntriesToTemporaryTable()
        {
            try
            {
                Log.Verbose1("DataLoggerStoreWrapper", "Copy new entries to data logger temporary table.");

                string query = $"CREATE TEMPORARY TABLE \"##tempDataLoggerTable\" AS " +
                               $"SELECT * " +
                               $"FROM \"{tableName}\" " +
                               $"WHERE \"Id\" IS NOT NULL " +
                               $"ORDER BY \"Timestamp\" ASC ";

                if (store.Status == StoreStatus.Online)
                    store.Query(query, out _, out _);
            }
            catch (Exception e)
            {
                Log.Error("DataLoggerStoreWrapper", $"Failed to create internal temporary table: {e.Message}.");
                throw;
            }
        }

        private void CopyNewEntriesToTemporaryTableUsingId(UInt64 rowId)
        {
            try
            {
                Int64 id = rowId == Int64.MaxValue ? -1 : (Int64)rowId; // -1 to consider also id = 0
                Log.Verbose1("DataLoggerStoreWrapper", $"Copy new entries to data logger temporary table with id greater than {id}.");

                string query = $"CREATE TEMPORARY TABLE \"##tempDataLoggerTable\" AS " +
                               $"SELECT * " +
                               $"FROM \"{tableName}\" " +
                               $"WHERE \"Id\" > {id} " +
                               $"ORDER BY \"Timestamp\" ASC ";

                if (store.Status == StoreStatus.Online)
                    store.Query(query, out _, out _);
            }
            catch (Exception e)
            {
                Log.Error("DataLoggerStoreWrapper", $"Failed to create internal temporary table: {e.Message}.");
                throw;
            }
        }

        public void DeleteTemporaryTable()
        {
            object[,] resultSet;
            string[] header;

            try
            {
                Log.Verbose1("DataLoggerStoreWrapper", "Delete data logger temporary table.");
                string query = $"DROP TABLE \"##tempDataLoggerTable\"";
                store.Query(query, out header, out resultSet);
            }
            catch (Exception e)
            {
                Log.Error("DataLoggerStoreWrapper", $"Failed to delete internal temporary table: {e.Message}.");
                throw;
            }
        }

        private List<DataLoggerRecord> QueryNewEntriesFromTemporaryTable()
        {
            List<DataLoggerRecord> records = null;
            object[,] resultSet;
            string[] header;

            try
            {
                string query = $"SELECT {GetQuerySelectParameters()} " +
                               $"FROM \"##tempDataLoggerTable\"";

                if (store.Status == StoreStatus.Online)
                {
                    store.Query(query, out header, out resultSet);
                    records = DataLoggerRecordUtils.GetDataLoggerRecordsFromQueryResult(resultSet,
                                                                                        header,
                                                                                        variablesToLogList,
                                                                                        insertOpCode,
                                                                                        insertVariableTimestamp,
                                                                                        logLocalTime);
                }
                else
                    records = new List<DataLoggerRecord>();

                Log.Verbose1("DataLoggerStoreWrapper", $"Query new entries from data logger temporary table (records count={records.Count}, query={query}).");
            }
            catch (Exception e)
            {
                Log.Error("DataLoggerStoreWrapper", $"Failed to query the internal temporary table: {e.Message}.");
                throw;
            }

            return records;
        }

        private string GetQuerySelectParameters()
        {
            var selectParameters = "\"Timestamp\", ";
            if (logLocalTime)
                selectParameters += "\"LocalTimestamp\", ";

            selectParameters = $"{selectParameters} {GetQueryColumnsOrderedByVariableName()}";

            return selectParameters;
        }

        private string GetQueryColumnsOrderedByVariableName()
        {
            var columnsOrderedByVariableName = string.Empty;
            foreach (var variable in variablesToLogList)
            {
                if (columnsOrderedByVariableName != string.Empty)
                    columnsOrderedByVariableName += ", ";

                columnsOrderedByVariableName += "\"" + variable.BrowseName + "\"";

                if (insertVariableTimestamp)
                    columnsOrderedByVariableName += ", \"" + variable.BrowseName + "_Timestamp\"";

                if (insertOpCode)
                    columnsOrderedByVariableName += ", \"" + variable.BrowseName + "_OpCode\"";
            }

            return columnsOrderedByVariableName;
        }

        public void DeleteRecords(int numberOfRecordsToDelete)
        {
            try
            {

                string query = $"DELETE FROM \"{tableName}\" " +
                               $"ORDER BY \"Timestamp\" ASC " +
                               $"LIMIT {numberOfRecordsToDelete}";

                store.Query(query, out _, out _);
            }
            catch (Exception e)
            {
                Log.Error("DataLogger Store", $"Failed to delete data from DataLogger store: {e.Message}.");
                throw;
            }
        }

        private readonly Store store;
        private readonly string tableName;
        private readonly List<VariableToLog> variablesToLogList;
        private readonly bool insertOpCode;
        private readonly bool insertVariableTimestamp;
        private readonly bool logLocalTime;
    }

    public interface SupportStore
    {
        void InsertRecords(List<Record> records);
        void DeleteRecords(int numberOfRecordsToDelete);
        long RecordsCount();
        List<Record> QueryOlderEntries(int numberOfEntries);
    }

    public class PushAgentStoreDataLoggerWrapper : SupportStore
    {
        public PushAgentStoreDataLoggerWrapper(Store store,
                                               string tableName,
                                               List<VariableToLog> variablesToLogList,
                                               bool insertOpCode,
                                               bool insertVariableTimestamp,
                                               bool logLocalTime)
        {
            this.store = store;
            this.tableName = tableName;
            this.variablesToLogList = variablesToLogList;
            this.insertOpCode = insertOpCode;
            this.insertVariableTimestamp = insertVariableTimestamp;
            this.logLocalTime = logLocalTime;

            try
            {
                CreateTable();
                table = GetTable();
                CreateColumns();
                CreateColumnIndex("Id", true);
                CreateColumnIndex("Timestamp", false);
                columns = GetTableColumnsOrderedByVariableName();
                idCount = GetMaxId();
            }
            catch (Exception e)
            {
                Log.Error("PushAgentStoreDataLoggerWrapper", $"Unable to create PushAgent store: {e.Message}.");
                throw;
            }
        }

        public void DeleteRecords(int numberOfRecordsToDelete)
        {
            try
            {

                string query = $"DELETE FROM \"{tableName}\" " +
                               $"ORDER BY \"Timestamp\" ASC, \"Id\" ASC " +
                               $"LIMIT {numberOfRecordsToDelete}";

                store.Query(query, out _, out _);
            }
            catch (Exception e)
            {
                Log.Error("PushAgentStoreDataLoggerWrapper", $"Failed to delete data from PushAgent store: {e.Message}.");
                throw;
            }
        }

        public void InsertRecords(List<Record> records)
        {
            List<DataLoggerRecord> dataLoggerRecords = records.Cast<DataLoggerRecord>().ToList();
            object[,] values = new object[records.Count, columns.Length];
            ulong tempIdCount = idCount;
            for (int i = 0; i < dataLoggerRecords.Count; ++i)
            {
                int j = 0;
                values[i, j++] = tempIdCount;
                values[i, j++] = dataLoggerRecords[i].timestamp;
                if (logLocalTime)
                    values[i, j++] = dataLoggerRecords[i].localTimestamp;

                foreach (var variable in dataLoggerRecords.ElementAt(i).variables)
                {
                    values[i, j++] = variable.value?.Value;
                    if (insertVariableTimestamp)
                        values[i, j++] = variable.timestamp;
                    if (insertOpCode)
                        values[i, j++] = variable.variableOpCode;
                }

                tempIdCount = GetNextInternalId(tempIdCount);
            }

            try
            {
                table.Insert(columns, values);
                idCount = tempIdCount;          // If all record are inserted then we update the idCount
            }
            catch (Exception e)
            {
                Log.Error("PushAgentStoreDataLoggerWrapper", $"Failed to insert data into PushAgent store: {e.Message}.");
                throw;
            }
        }

        public List<Record> QueryOlderEntries(int numberOfEntries)
        {
            List<Record> records = null;
            object[,] resultSet;
            string[] header;

            try
            {
                string query = $"SELECT {GetQuerySelectParameters()} " +
                               $"FROM \"{tableName}\" " +
                               $"ORDER BY \"Timestamp\" ASC, \"Id\" ASC " +
                               $"LIMIT {numberOfEntries}";

                store.Query(query, out header, out resultSet);
                records = DataLoggerRecordUtils.GetDataLoggerRecordsFromQueryResult(resultSet,
                                                                                    header,
                                                                                    variablesToLogList,
                                                                                    insertOpCode,
                                                                                    insertVariableTimestamp,
                                                                                    logLocalTime).Cast<Record>().ToList();
            }
            catch (Exception e)
            {
                Log.Error("PushAgentStoreDataLoggerWrapper", $"Failed to query older entries from PushAgent store: {e.Message}.");
                throw;
            }

            return records;
        }

        public long RecordsCount()
        {
            object[,] resultSet;
            long result = 0;

            try
            {
                string query = $"SELECT COUNT(*) FROM \"{tableName}\"";
                store.Query(query, out _, out resultSet);
                result = ((long)resultSet[0, 0]);
            }
            catch (Exception e)
            {
                Log.Error("PushAgentStoreDataLoggerWrapper", $"Failed to query count: {e.Message}.");
                throw;
            }

            return result;
        }

        private UInt64 GetMaxId()
        {
            object[,] resultSet;

            try
            {
                string query = $"SELECT MAX(\"Id\") FROM \"{tableName}\"";
                store.Query(query, out _, out resultSet);

                if (resultSet[0, 0] != null)
                    return GetNextInternalId(UInt64.Parse(resultSet[0, 0].ToString()));
                else
                    return 0;
            }
            catch (Exception e)
            {
                Log.Error("PushAgentStoreDataLoggerWrapper", $"Failed to query maxid: {e.Message}.");
                throw;
            }
        }

        private UInt64 GetNextInternalId(UInt64 currentId)
        {
            return currentId < Int64.MaxValue ? currentId + 1 : 0;
        }

        private void CreateTable()
        {
            try
            {
                store.AddTable(tableName);
            }
            catch (Exception e)
            {
                Log.Error("PushAgentStoreDataLoggerWrapper", $"Unable to create PushAgent store: {e.Message}.");
                throw;
            }
        }

        private Table GetTable()
        {
            return store.Tables.FirstOrDefault(t => t.BrowseName == tableName);
        }

        private void CreateColumns()
        {
            try
            {
                table.AddColumn("Id", OpcUa.DataTypes.UInt64);
                table.AddColumn("Timestamp", OpcUa.DataTypes.DateTime);
                if (logLocalTime)
                    table.AddColumn("LocalTimestamp", OpcUa.DataTypes.DateTime);

                foreach (var variableToLog in variablesToLogList)
                {
                    table.AddColumn(variableToLog.BrowseName, variableToLog.ActualDataType);

                    if (insertVariableTimestamp)
                        table.AddColumn(variableToLog.BrowseName + "_Timestamp", OpcUa.DataTypes.DateTime);

                    if (insertOpCode)
                        table.AddColumn(variableToLog.BrowseName + "_OpCode", OpcUa.DataTypes.Int32);
                }
            }
            catch (Exception e)
            {
                Log.Error("PushAgentStoreDataLoggerWrapper", $"Unable to create columns of PushAgent store: {e.Message}.");
                throw;
            }
        }

        private void CreateColumnIndex(string columnName, bool unique)
        {
            string uniqueKeyWord = string.Empty;
            if (unique)
                uniqueKeyWord = "UNIQUE";
            try
            {
                string query = $"CREATE {uniqueKeyWord} INDEX \"{columnName}_index\" ON  \"{tableName}\"(\"{columnName}\")";
                store.Query(query, out _, out _);
            }
            catch (Exception e)
            {
                Log.Warning("PushAgentStoreDataLoggerWrapper", $"Unable to create index on PushAgent store: {e.Message}.");
            }
        }

        private string[] GetTableColumnsOrderedByVariableName()
        {
            List<string> columnNames = new List<string>();
            columnNames.Add("Id");
            columnNames.Add("Timestamp");
            if (logLocalTime)
                columnNames.Add("LocalTimestamp");

            foreach (var variableToLog in variablesToLogList)
            {
                columnNames.Add(variableToLog.BrowseName);

                if (insertVariableTimestamp)
                    columnNames.Add(variableToLog.BrowseName + "_Timestamp");

                if (insertOpCode)
                    columnNames.Add(variableToLog.BrowseName + "_OpCode");
            }

            return columnNames.ToArray();
        }

        private string GetQuerySelectParameters()
        {
            var selectParameters = "\"Timestamp\", ";
            if (logLocalTime)
                selectParameters += "\"LocalTimestamp\", ";

            selectParameters = $"{selectParameters} {GetQueryColumnsOrderedByVariableName()}";

            return selectParameters;
        }

        private string GetQueryColumnsOrderedByVariableName()
        {
            string columnsOrderedByVariableName = string.Empty;
            foreach (var variable in variablesToLogList)
            {
                if (columnsOrderedByVariableName != string.Empty)
                    columnsOrderedByVariableName += ", ";

                columnsOrderedByVariableName += "\"" + variable.BrowseName + "\"";

                if (insertVariableTimestamp)
                    columnsOrderedByVariableName += ", \"" + variable.BrowseName + "_Timestamp\"";

                if (insertOpCode)
                    columnsOrderedByVariableName += ", \"" + variable.BrowseName + "_OpCode\"";
            }

            return columnsOrderedByVariableName;
        }

        private readonly Store store;
        private readonly Table table;
        private readonly string tableName;
        private readonly List<VariableToLog> variablesToLogList;
        private readonly string[] columns;
        private readonly bool insertOpCode;
        private readonly bool insertVariableTimestamp;
        private readonly bool logLocalTime;
        private UInt64 idCount;
    }

    public class PushAgentStoreRowPerVariableWrapper : SupportStore
    {
        public PushAgentStoreRowPerVariableWrapper(SQLiteStore store, string tableName, bool insertOpCode)
        {
            this.store = store;
            this.tableName = tableName;
            this.insertOpCode = insertOpCode;

            try
            {
                CreateTable();
                table = GetTable();
                CreateColumns();
                CreateColumnIndex("Id", true);
                CreateColumnIndex("Timestamp", false);
                columns = GetTableColumnNames();
                idCount = GetMaxId();
            }
            catch (Exception e)
            {
                Log.Error("PushAgentStoreRowPerVariableWrapper", $"Unable to create PushAgent store: {e.Message}.");
                throw;
            }
        }

        // Updated July 2024 to support ARP, added condition to delete non-ARP data only when requested record count < clearTempStore command count
        public void DeleteRecords(int numberOfRecordsToDelete)
        {
            try
            {
                string query;
                if (numberOfRecordsToDelete < 100000000) 
                    query = $"DELETE FROM \"{tableName}\" " +
                               $"WHERE \"VariableId\" NOT LIKE '%FiixARP1_JSON%' " +
                               $"ORDER BY \"Timestamp\" ASC, \"Id\" ASC " +
                               $"LIMIT {numberOfRecordsToDelete}";
                else query = $"DELETE FROM \"{tableName}\" " +
                               $"ORDER BY \"Timestamp\" ASC, \"Id\" ASC " +
                               $"LIMIT {numberOfRecordsToDelete}";
                store.Query(query, out _, out _);
            }
            catch (Exception e)
            {
                Log.Error("PushAgentStoreRowPerVariableWrapper", $"Failed to delete data from PushAgent store: {e.Message}.");
                throw;
            }
        }

        // Updated July 2024 to support ARP, added condition to delete ARP data only
        public void DeleteARPRecords(int numberOfRecordsToDelete)
        {
            try
            {
                string query = $"DELETE FROM \"{tableName}\" " +
                               $"WHERE \"VariableId\" LIKE '%FiixARP1_JSON%' " +
                               $"ORDER BY \"Timestamp\" ASC, \"Id\" ASC " +
                               $"LIMIT {numberOfRecordsToDelete}";

                store.Query(query, out _, out _);
            }
            catch (Exception e)
            {
                Log.Error("PushAgentStoreRowPerVariableWrapper", $"Failed to delete data from PushAgent store: {e.Message}.");
                throw;
            }
        }

        public void InsertRecords(List<Record> records)
        {
            List<VariableRecord> variableRecords = records.Cast<VariableRecord>().ToList();
            object[,] values = new object[records.Count, columns.Length];
            UInt64 tempIdCount = idCount;
            for (int i = 0; i < variableRecords.Count; ++i)
            {
                values[i, 0] = tempIdCount;
                values[i, 1] = variableRecords[i].timestamp.Value;
                values[i, 2] = variableRecords[i].variableId;
                values[i, 3] = variableRecords[i].serializedValue;
                if (insertOpCode)
                    values[i, 4] = variableRecords[i].variableOpCode;

                tempIdCount = GetNextInternalId(tempIdCount);
            }

            try
            {
                table.Insert(columns, values);
                idCount = tempIdCount;
            }
            catch (Exception e)
            {
                Log.Error("PushAgentStoreRowPerVariableWrapper", $"Failed to insert data into PushAgent store: {e.Message}.");
                throw;
            }
        }

        public List<Record> QueryOlderEntries(int numberOfEntries)
        {
            List<VariableRecord> records = new List<VariableRecord>();
            object[,] resultSet;
            string[] header;

            try
            {
                string query = $"SELECT {GetQueryColumns()} " +
                               $"FROM \"{tableName}\" " +
                               $"ORDER BY \"Timestamp\" ASC, \"Id\" ASC " +
                               $"LIMIT {numberOfEntries}";

                store.Query(query, out header, out resultSet);

                var rowCount = resultSet != null ? resultSet.GetLength(0) : 0;
                for (int i = 0; i < rowCount; ++i)
                {
                    int? opCodeValue = (int?)null;
                    if (insertOpCode)
                    {
                        if (resultSet[i, 3] == null)
                            opCodeValue = null;
                        else
                            opCodeValue = int.Parse(resultSet[i, 3].ToString());
                    }

                    VariableRecord record;
                    if (insertOpCode)
                        record = new VariableRecord(GetTimestamp(resultSet[i, 0]),
                                                    resultSet[i, 1].ToString(),
                                                    null,
                                                    resultSet[i, 2].ToString(),
                                                    opCodeValue);
                    else
                        record = new VariableRecord(GetTimestamp(resultSet[i, 0]),
                                                    resultSet[i, 1].ToString(),
                                                    null,
                                                    resultSet[i, 2].ToString());
                    records.Add(record);
                }
            }
            catch (Exception e)
            {
                Log.Error("PushAgentStoreRowPerVariableWrapper", $"Failed to query older entries from PushAgent store: {e.Message}.");
                throw;
            }

            return records.Cast<Record>().ToList();
        }

        public long RecordsCount()
        {
            object[,] resultSet;
            long result = 0;

            try
            {
                string query = $"SELECT COUNT(*) FROM \"{tableName}\"";

                store.Query(query, out _, out resultSet);
                result = ((long)resultSet[0, 0]);
            }
            catch (Exception e)
            {
                Log.Error("PushAgentStoreRowPerVariableWrapper", $"Failed to query count: {e.Message}.");
                throw;
            }

            return result;
        }

        private ulong GetMaxId()
        {
            object[,] resultSet;

            try
            {
                string query = $"SELECT MAX(\"ID\") FROM \"{tableName}\"";

                store.Query(query, out _, out resultSet);

                if (resultSet[0, 0] != null)
                    return GetNextInternalId(UInt64.Parse(resultSet[0, 0].ToString()));
                else
                    return 0;
            }
            catch (Exception e)
            {
                Log.Error("PushAgentStoreRowPerVariableWrapper", $"Failed to query maxid: {e.Message}.");
                throw;
            }
        }

        private UInt64 GetNextInternalId(UInt64 currentId)
        {
            return currentId < Int64.MaxValue ? currentId + 1 : 0;
        }

        private void CreateTable()
        {
            try
            {
                store.AddTable(tableName);
            }
            catch (Exception e)
            {
                Log.Error("PushAgentStoreRowPerVariableWrapper", $"Unable to create PushAgent store: {e.Message}.");
                throw;
            }
        }

        private Table GetTable()
        {
            return store.Tables.FirstOrDefault(t => t.BrowseName == tableName);
        }

        private void CreateColumns()
        {
            try
            {
                table.AddColumn("Id", OpcUa.DataTypes.UInt64);
                table.AddColumn("Timestamp", OpcUa.DataTypes.DateTime);
                table.AddColumn("VariableId", OpcUa.DataTypes.String);
                table.AddColumn("Value", OpcUa.DataTypes.String);

                if (insertOpCode)
                    table.AddColumn("OpCode", OpcUa.DataTypes.Int32);
            }
            catch (Exception e)
            {
                Log.Error("PushAgentStoreRowPerVariableWrapper", $"Unable to create columns of PushAgent store: {e.Message}.");
                throw;
            }
        }

        private void CreateColumnIndex(string columnName, bool unique)
        {
            string uniqueKeyWord = string.Empty;
            if (unique)
                uniqueKeyWord = "UNIQUE";
            try
            {
                string query = $"CREATE {uniqueKeyWord} INDEX \"{columnName}_index\" ON  \"{tableName}\"(\"{columnName}\")";
                store.Query(query, out _, out _);
            }
            catch (Exception e)
            {
                Log.Warning("PushAgentStoreRowPerVariableWrapper", $"Unable to create index on PushAgent store: {e.Message}.");
            }
        }

        private string[] GetTableColumnNames()
        {
            if (table == null)
                return null;

            var result = new List<string>();
            foreach (var column in table.Columns)
                result.Add(column.BrowseName);

            return result.ToArray();
        }

        private string GetQueryColumns()
        {
            string columns = "\"Timestamp\", ";
            columns += "\"VariableId\", ";
            columns += "\"Value\"";

            if (insertOpCode)
                columns += ", OpCode";

            return columns;
        }

        private DateTime GetTimestamp(object value)
        {
            if (Type.GetTypeCode(value.GetType()) == TypeCode.DateTime)
                return ((DateTime)value);
            else
                return DateTime.SpecifyKind(DateTime.Parse(value.ToString()), DateTimeKind.Utc);
        }

        private readonly SQLiteStore store;
        private readonly string tableName;
        private readonly Table table;
        private readonly string[] columns;
        private readonly bool insertOpCode;
        private UInt64 idCount;
    }

    public class DataLoggerStatusStoreWrapper
    {
        public DataLoggerStatusStoreWrapper(Store store,
                                            string tableName,
                                            List<VariableToLog> variablesToLogList,
                                            bool insertOpCode,
                                            bool insertVariableTimestamp)
        {
            this.store = store;
            this.tableName = tableName;
            this.variablesToLogList = variablesToLogList;
            this.insertOpCode = insertOpCode;
            this.insertVariableTimestamp = insertVariableTimestamp;

            try
            {
                CreateTable();
                table = GetTable();
                CreateColumns();
                columns = GetTableColumnsOrderedByVariableName();
            }
            catch (Exception e)
            {
                Log.Error("DataLoggerStatusStoreWrapper", $"Unable to initialize internal DataLoggerStatusStoreWrapper {e.Message}.");
                throw;
            }
        }

        public void UpdateRecord(UInt64 rowId)
        {
            if (RecordsCount() == 0)
            {
                InsertRecord(rowId);
                return;
            }

            try
            {
                string query = $"UPDATE \"{tableName}\" SET \"RowId\" = {rowId} WHERE \"Id\"= 1";
                Log.Verbose1("DataLoggerStatusStoreWrapper", $"Update data logger status row id to {rowId}.");

                store.Query(query, out _, out _);
            }
            catch (Exception e)
            {
                Log.Error("DataLoggerStatusStoreWrapper", $"Failed to update internal data logger status: {e.Message}.");
                throw;
            }
        }

        public void InsertRecord(UInt64 rowId)
        {
            var values = new object[1, columns.Length];

            values[0, 0] = 1;
            values[0, 1] = rowId;

            try
            {
                Log.Verbose1("DataLoggerStatusStoreWrapper", $"Set data logger status row id to {rowId}.");
                table.Insert(columns, values);
            }
            catch (Exception e)
            {
                Log.Error("DataLoggerStatusStoreWrapper", $"Failed to update internal data logger status: {e.Message}.");
                throw;
            }
        }

        public UInt64? QueryStatus()
        {
            object[,] resultSet;
            string[] header;

            try
            {
                string query = $"SELECT \"RowId\" FROM \"{tableName}\"";

                store.Query(query, out header, out resultSet);

                if (resultSet[0, 0] != null)
                {
                    Log.Verbose1("DataLoggerStatusStoreWrapper", $"Query data logger status returns {resultSet[0, 0]}.");
                    return UInt64.Parse(resultSet[0, 0].ToString());
                }
                return null;
            }
            catch (Exception e)
            {
                Log.Error("DataLoggerStatusStoreWrapper", $"Failed to query internal data logger status: {e.Message}.");
                throw;
            }
        }

        public long RecordsCount()
        {
            object[,] resultSet;
            long result = 0;

            try
            {
                string query = $"SELECT COUNT(*) FROM \"{tableName}\"";

                store.Query(query, out _, out resultSet);
                result = ((long)resultSet[0, 0]);
                Log.Verbose1("DataLoggerStatusStoreWrapper", $"Get data logger status records count returns {result}.");
            }
            catch (Exception e)
            {
                Log.Error("DataLoggerStatusStoreWrapper", $"Failed to query count: {e.Message}.");
                throw;
            }

            return result;
        }

        private void CreateTable()
        {
            try
            {
                store.AddTable(tableName);
            }
            catch (Exception e)
            {
                Log.Error("DataLoggerStatusStoreWrapper", $"Unable to create internal table to DataLoggerStatusStore: {e.Message}.");
                throw;
            }
        }

        private Table GetTable()
        {
            return store.Tables.FirstOrDefault(t => t.BrowseName == tableName);
        }

        private void CreateColumns()
        {
            try
            {
                table.AddColumn("Id", OpcUa.DataTypes.Int32);

                // We need to store only the last query's last row's id to retrieve the dataLogger row
                table.AddColumn("RowId", OpcUa.DataTypes.Int64);
            }
            catch (Exception e)
            {
                Log.Error("DataLoggerStatusStoreWrapper", $"Unable to create columns of internal DataLoggerStatusStore: {e.Message}.");
                throw;
            }
        }

        private string[] GetTableColumnsOrderedByVariableName()
        {
            List<string> columnNames = new List<string>();
            columnNames.Add("Id");
            columnNames.Add("RowId");

            return columnNames.ToArray();
        }

        private readonly Store store;
        private readonly Table table;
        private readonly string tableName;
        private readonly List<VariableToLog> variablesToLogList;
        private readonly string[] columns;
        private readonly bool insertOpCode;
        private readonly bool insertVariableTimestamp;
    }

    public class DataLoggerRecordPuller
    {
        public DataLoggerRecordPuller(IUAObject logicObject,
                                      NodeId dataLoggerNodeId,
                                      SupportStore pushAgentStore,
                                      DataLoggerStatusStoreWrapper statusStoreWrapper,
                                      DataLoggerStoreWrapper dataLoggerStore,
                                      bool preserveDataLoggerHistory,
                                      bool pushByRow,
                                      int pullPeriod,
                                      int numberOfVariablesToLog)
        {
            this.logicObject = logicObject;
            this.pushAgentStore = pushAgentStore;
            this.statusStoreWrapper = statusStoreWrapper;
            this.dataLoggerStore = dataLoggerStore;
            this.dataLoggerNodeId = dataLoggerNodeId;
            this.preserveDataLoggerHistory = preserveDataLoggerHistory;
            this.pushByRow = pushByRow;
            this.numberOfVariablesToLog = numberOfVariablesToLog;

            if (this.preserveDataLoggerHistory)
            {
                UInt64? dataLoggerMaxId = this.dataLoggerStore.GetDataLoggerMaxId();

                if (statusStoreWrapper.RecordsCount() == 1)
                    lastPulledRecordId = statusStoreWrapper.QueryStatus();

                // Check if DataLogger has elements or if the maximum id is greater than lastPulledRecordId
                if (dataLoggerMaxId == null || (dataLoggerMaxId.HasValue && dataLoggerMaxId < lastPulledRecordId))
                    lastPulledRecordId = Int64.MaxValue;  // We have no elements in DataLogger so we will restart the count from 0
            }

            lastInsertedValues = new Dictionary<string, UAValue>();

            dataLoggerPullTask = new PeriodicTask(PullDataLoggerRecords, pullPeriod, this.logicObject);
            dataLoggerPullTask.Start();
        }

        public DataLoggerRecordPuller(IUAObject logicObject,
                                      NodeId dataLoggerNodeId,
                                      SupportStore pushAgentStore,
                                      DataLoggerStoreWrapper dataLoggerStore,
                                      bool preserveDataLoggerHistory,
                                      bool pushByRow,
                                      int pullPeriod,
                                      int numberOfVariablesToLog)
        {
            this.logicObject = logicObject;
            this.pushAgentStore = pushAgentStore;
            this.dataLoggerStore = dataLoggerStore;
            this.dataLoggerNodeId = dataLoggerNodeId;
            this.preserveDataLoggerHistory = preserveDataLoggerHistory;
            this.pushByRow = pushByRow;
            this.numberOfVariablesToLog = numberOfVariablesToLog;

            lastInsertedValues = new Dictionary<string, UAValue>();

            dataLoggerPullTask = new PeriodicTask(PullDataLoggerRecords, pullPeriod, this.logicObject);
            dataLoggerPullTask.Start();
        }

        public void StopPullTask()
        {
            dataLoggerPullTask.Cancel();
        }

        private void PullDataLoggerRecords()
        {
            try
            {
                dataLoggerPulledRecords = null;
                if (!preserveDataLoggerHistory || lastPulledRecordId == null)
                    dataLoggerPulledRecords = dataLoggerStore.QueryNewEntries();
                else
                    dataLoggerPulledRecords = dataLoggerStore.QueryNewEntriesUsingLastQueryId(lastPulledRecordId.Value);

                if (dataLoggerPulledRecords.Count > 0)
                {
                    Log.Info("Fiix Gateway","Pulling " + dataLoggerPulledRecords.Count + " meter reading and arp records from DataLogger to AgentStore, lastPulledRecordId: " + (lastPulledRecordId == null ? "null" : lastPulledRecordId.Value));
                    InsertDataLoggerRecordsIntoPushAgentStore();

                    if (!preserveDataLoggerHistory)
                        dataLoggerStore.DeletePulledRecords();
                    else
                    {
                        lastPulledRecordId = dataLoggerStore.GetMaxIdFromTemporaryTable();

                        statusStoreWrapper.UpdateRecord(lastPulledRecordId.Value);
                    }

                    dataLoggerPulledRecords.Clear();
                }
            }
            catch (Exception e)
            {
                if (dataLoggerStore.GetStoreStatus() != StoreStatus.Offline)
                {
                    Log.Error("DataLoggerRecordPuller", $"Unable to retrieve data from DataLogger store: {e.Message}.");
                    StopPullTask();
                }
            }
        }

        private void InsertDataLoggerRecordsIntoPushAgentStore()
        {
            if (!IsStoreSpaceAvailable())
            {
                Log.Warning("InsertDataLoggerRecordsIntoPushAgentStore, no store space available.");
                return;
            }

            if (pushByRow)
                InsertRowsIntoPushAgentStore();
            else
                InsertVariableRecordsIntoPushAgentStore();
        }

        private VariableRecord CreateVariableRecord(VariableRecord variable, DateTime recordTimestamp)
        {
            VariableRecord variableRecord;
            if (variable.timestamp == null)
                variableRecord = new VariableRecord(recordTimestamp,
                                                    variable.variableId,
                                                    variable.value,
                                                    variable.serializedValue,
                                                    variable.variableOpCode);
            else
                variableRecord = new VariableRecord(variable.timestamp,
                                                    variable.variableId,
                                                    variable.value,
                                                    variable.serializedValue,
                                                    variable.variableOpCode);



            return variableRecord;
        }

        private void InsertRowsIntoPushAgentStore()
        {
            int numberOfStorableRecords = CalculateNumberOfElementsToInsert();

            if (dataLoggerPulledRecords.Count > 0)
                pushAgentStore.InsertRecords(dataLoggerPulledRecords.Cast<Record>().ToList().GetRange(0, numberOfStorableRecords));
        }

        private void InsertVariableRecordsIntoPushAgentStore()
        {
            int numberOfStorableRecords = CalculateNumberOfElementsToInsert();

            // Temporary dictionary is used to update values, once the records are inserted then the content is copied to lastInsertedValues
            Dictionary<string, UAValue> tempLastInsertedValues = lastInsertedValues.Keys.ToDictionary(_ => _, _ => lastInsertedValues[_]);
            List<VariableRecord> pushAgentRecords = new List<VariableRecord>();
            bool isARPenabled = logicObject.GetVariable("Cfg_ARP_enabled") != null ? (bool)logicObject.GetVariable("Cfg_ARP_enabled").Value : false;

            foreach (var record in dataLoggerPulledRecords.GetRange(0, numberOfStorableRecords))
            {
                foreach (var variable in record.variables)
                {     
                    // Fiix ARP support added July 2024. If the variable is ARP context, ARP source value designed not to send, or disable JSON output; skip adding to pushagent.
                    if (variable.variableId.Contains("FiixARP0") ) continue;
                    if (!isARPenabled && variable.variableId.Contains("FiixARP1_JSON")) continue;
                
                    VariableRecord variableRecord = CreateVariableRecord(variable, record.timestamp.Value);
                    if (GetSamplingMode() == SamplingMode.VariableChange)
                    {
                        if (!tempLastInsertedValues.ContainsKey(variable.variableId))
                        {
                            if (variableRecord.serializedValue != null)
                            {
                                pushAgentRecords.Add(variableRecord);
                                tempLastInsertedValues.Add(variableRecord.variableId, variableRecord.value);
                            }
                        }
                        else
                        {
                            if (variable.value != tempLastInsertedValues[variable.variableId] && variableRecord.serializedValue != null)
                            {
                                pushAgentRecords.Add(variableRecord);
                                tempLastInsertedValues[variableRecord.variableId] = variableRecord.value;
                            }
                        }
                    }
                    else
                    {
                        if (variableRecord.serializedValue != null)
                            pushAgentRecords.Add(variableRecord);
                    }
                }
             }

            if (pushAgentRecords.Count > 0)
            {
                pushAgentStore.InsertRecords(pushAgentRecords.Cast<Record>().ToList());

                if (GetSamplingMode() == SamplingMode.VariableChange)
                    lastInsertedValues = tempLastInsertedValues.Keys.ToDictionary(_ => _, _ => tempLastInsertedValues[_]);
            }
        }

        private int GetMaximumStoreCapacity()
        {
            return logicObject.GetVariable("Cfg_MaximumStoreCapacity").Value;
        }

        private SamplingMode GetSamplingMode()
        {
            var dataLogger = InformationModel.Get<DataLogger>(dataLoggerNodeId);
            return dataLogger.SamplingMode;
        }

        private int CalculateNumberOfElementsToInsert()
        {
            // Calculate the number of records that can be effectively stored
            int numberOfStorableRecords;

            if (pushByRow)
                numberOfStorableRecords = (GetMaximumStoreCapacity() - (int)pushAgentStore.RecordsCount());
            else
            {
                if (GetSamplingMode() == SamplingMode.VariableChange)
                    numberOfStorableRecords = (GetMaximumStoreCapacity() - (int)pushAgentStore.RecordsCount());
                else
                    numberOfStorableRecords = (int)Math.Floor((double)(GetMaximumStoreCapacity() - (int)pushAgentStore.RecordsCount()) / numberOfVariablesToLog);
            }

            if (numberOfStorableRecords > dataLoggerPulledRecords.Count)
                numberOfStorableRecords = dataLoggerPulledRecords.Count;

            return numberOfStorableRecords;
        }

        private bool IsStoreSpaceAvailable()
        {
            if (pushAgentStore.RecordsCount() >= GetMaximumStoreCapacity() - 1)
            {
                Log.Warning("DataLoggerRecordPuller", "Maximum store capacity reached! Skipping...");
                return false;
            }

            var percentageStoreCapacity = ((double)pushAgentStore.RecordsCount() / GetMaximumStoreCapacity()) * 100;
            if (percentageStoreCapacity >= 70)
                Log.Warning("DataLoggerRecordPuller", "Store capacity 70% reached!");

            return true;
        }

        private List<DataLoggerRecord> dataLoggerPulledRecords;
        private UInt64? lastPulledRecordId;
        private readonly PeriodicTask dataLoggerPullTask;
        private readonly SupportStore pushAgentStore;
        private readonly DataLoggerStatusStoreWrapper statusStoreWrapper;
        private readonly DataLoggerStoreWrapper dataLoggerStore;
        private readonly bool preserveDataLoggerHistory;
        private readonly bool pushByRow;
        private readonly IUAObject logicObject;
        private readonly int numberOfVariablesToLog;
        private readonly NodeId dataLoggerNodeId;
        private Dictionary<string, UAValue> lastInsertedValues;
    }

    public class JSONBuilder
    {
        public JSONBuilder(bool insertOpCode, bool insertVariableTimestamp, bool logLocalTime)
        {
            this.insertOpCode = insertOpCode;
            this.insertVariableTimestamp = insertVariableTimestamp;
            this.logLocalTime = logLocalTime;
        }

        public string CreatePacketFormatJSON(DataLoggerRowPacket packet)
        {
            var sb = new StringBuilder();
            var sw = new StringWriter(sb);
            using (var writer = new JsonTextWriter(sw))
            {

                writer.Formatting = Formatting.None;

                writer.WriteStartObject();
                writer.WritePropertyName("Timestamp");
                writer.WriteValue(packet.timestamp);
                writer.WritePropertyName("ClientId");
                writer.WriteValue(packet.clientId);
                writer.WritePropertyName("Rows");
                writer.WriteStartArray();
                foreach (var record in packet.records)
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("RowTimestamp");
                    writer.WriteValue(record.timestamp);

                    if (logLocalTime)
                    {
                        writer.WritePropertyName("RowLocalTimestamp");
                        writer.WriteValue(record.localTimestamp);
                    }

                    writer.WritePropertyName("Variables");
                    writer.WriteStartArray();
                    foreach (var variable in record.variables)
                    {
                        writer.WriteStartObject();

                        writer.WritePropertyName("VariableName");
                        writer.WriteValue(variable.variableId);
                        writer.WritePropertyName("Value");
                        writer.WriteValue(variable.value?.Value);

                        if (insertVariableTimestamp)
                        {
                            writer.WritePropertyName("VariableTimestamp");
                            writer.WriteValue(variable.timestamp);
                        }

                        if (insertOpCode)
                        {
                            writer.WritePropertyName("VariableOpCode");
                            writer.WriteValue(variable.variableOpCode);
                        }

                        writer.WriteEndObject();
                    }
                    writer.WriteEnd();
                    writer.WriteEndObject();
                }
                writer.WriteEnd();
                writer.WriteEndObject();
            }

            return sb.ToString();
        }

        public string CreatePacketFormatJSON(VariablePacket packet)
        {
            var sb = new StringBuilder();
            var sw = new StringWriter(sb);
            using (var writer = new JsonTextWriter(sw))
            {
                writer.Formatting = Formatting.None;

                writer.WriteStartObject();
                writer.WritePropertyName("Timestamp");
                writer.WriteValue(packet.timestamp);
                writer.WritePropertyName("ClientId");
                writer.WriteValue(packet.clientId);
                writer.WritePropertyName("Records");
                writer.WriteStartArray();
                foreach (var record in packet.records)
                {
                    writer.WriteStartObject();

                    writer.WritePropertyName("VariableName");
                    writer.WriteValue(record.variableId);
                    writer.WritePropertyName("SerializedValue");
                    writer.WriteValue(record.serializedValue);
                    writer.WritePropertyName("VariableTimestamp");
                    writer.WriteValue(record.timestamp);

                    if (insertOpCode)
                    {
                        writer.WritePropertyName("VariableOpCode");
                        writer.WriteValue(record.variableOpCode);
                    }

                    writer.WriteEndObject();
                }
                writer.WriteEnd();
                writer.WriteEndObject();
            }

            return sb.ToString();
        }

        private readonly bool insertOpCode;
        private readonly bool insertVariableTimestamp;
        private readonly bool logLocalTime;
    }
}

