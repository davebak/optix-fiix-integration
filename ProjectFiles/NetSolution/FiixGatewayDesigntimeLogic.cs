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
using HttpAPIGateway;
using System.Runtime.CompilerServices;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Net.Http.Json;
using System.Linq;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.ComponentModel;
using FTOptix.WebUI;
using FTOptix.EventLogger;
using System.IO;
using Gpe.Integration.Fiix.Connector.Services;
using Gpe.Integration.Fiix.Connector.Models.Utilities;
using Gpe.Integration.Fiix.Connector.Configuration;
using Gpe.Integration.Fiix.Connector;
using Gpe.Integration.Fiix.Connector.Models;
#endregion
/*
Fiix Gateway designtime script hosting Fiix objects classes and fetch assets and their support data from Fiix, sync as Optix data model.
=============================================================

Disclaimer of Warranty
THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT ARE PROVIDED "AS IS" WITHOUT WARRANTIES OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION, ALL IMPLIED WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE, NON-INFRINGEMENT OR OTHER VIOLATION OF RIGHTS. ROCKWELL AUTOMATION DOES NOT WARRANT OR MAKE ANY REPRESENTATIONS REGARDING THE USE, VALIDITY, ACCURACY, OR RELIABILITY OF, OR THE RESULTS OF ANY USE OF, OR OTHERWISE RESPECTING, THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT OR ANY WEB SITE LINKED TO THIS DOCUMENT 

Limitation of Liability
UNDER NO CIRCUMSTANCE (INCLUDING NEGLIGENCE AND TO THE FULLEST EXTEND PERMITTED BY APPLICABLE LAW) WILL ROCKWELL AUTOMATION BE LIABLE FOR ANY DIRECT, INDIRECT, SPECIAL, INCIDENTAL, PUNITIVE OR CONSEQUENTIAL DAMAGES (INCLUDING WITHOUT LIMITATION, BUSINESS INTERRUPTION, DELAYS, LOSS OF DATA OR PROFIT) ARISING OUT OF THE USE OR THE INABILITY TO USE THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT EVEN IF ROCKWELL AUTOMATION HAS BEEN ADVISED OF THE POSSIBILITY OF SUCH DAMAGES. IF USE OF SUCH MATERIALS RESULTS IN THE NEED FOR SERVICING, REPAIR OR CORRECTION OF USER EQUIPMENT OR DATA, USER ASSUMES ANY COSTS ASSOCIATED THEREWITH.

Copyright © Rockwell Automation, Inc.  All Rights Reserved. 

=============================================================
*/
public class FiixGatewayDesigntimeLogic : BaseNetLogic
{
    int newCategoryCount = 0, updateCategoryCount = 0, newEventTypeCount = 0, updateEventTypeCount = 0;
    int newOfflineReasonCount = 0, updateOfflineReasonCount = 0, newOnlineReasonCount = 0, updateOnlineReasonCount = 0;
    int newPriorityCount = 0, updatePriorityCount = 0, newWOStatusCount = 0, updateWOStatusCount = 0, newMaintenanceTypeCount = 0, updateMaintenanceTypeCount = 0;

    [ExportMethod]
    public void SyncCategoriesTypesUnits()
    {
        categoryFolder = LogicObject.Owner.Owner.Find("AssetCategories");
        AssetCategoryType = LogicObject.Owner.Find("AssetCategory");
        eventTypeFolder = LogicObject.Owner.Owner.Find("AssetEventTypes");
        AssetEventTypeType = LogicObject.Owner.Find("AssetEventType");
        assetOfflineReasonFolder = LogicObject.Owner.Owner.Find("AssetOfflineReasons");
        assetOnlineReasonFolder = LogicObject.Owner.Owner.Find("AssetOnlineReasons");
        AssetOfflineReasonType = LogicObject.Owner.Find("AssetOfflineReason");
        priorityFolder = LogicObject.Owner.Owner.Find("Priorities");
        PriorityType = LogicObject.Owner.Find("Priority");
        woStatusFolder = LogicObject.Owner.Owner.Find("WorkOrderStatus");
        WOStatusType = LogicObject.Owner.Find("WOStatus");
        maintenanceTypeFolder = LogicObject.Owner.Owner.Find("MaintenanceTypes");
        MaintenanceTypeType = LogicObject.Owner.Find("MaintenanceType");

        // Assign MeterReading datalogger and its datastore, if has been set
        IUANode runtimeLogic = Owner.Find("FiixGatewayRuntimeLogic");
        DataLogger meterReadingDataLogger = Owner.Owner.Find<DataLogger>("MeterReadingDataLogger");
        Store meterReadingDataStore = Owner.Owner.Find<Store>("MeterReadingDataStore");
        if (runtimeLogic != null && runtimeLogic.GetVariable("Cfg_DataLogger") != null)
        {
            NodeId cfg_dataLoggerId = (NodeId)runtimeLogic.GetVariable("Cfg_DataLogger").Value;
            if (cfg_dataLoggerId == null || InformationModel.GetObject(cfg_dataLoggerId) == null)
            {
                if (meterReadingDataLogger != null) runtimeLogic.GetVariable("Cfg_DataLogger").Value = meterReadingDataLogger.NodeId;
                Log.Info("Fiix Library initializing", "assigning DataLogger " + meterReadingDataLogger.BrowseName + " to RuntimeLogic");
            }
            var cfg_dataStoreId = meterReadingDataLogger.Store;
            if (cfg_dataStoreId == null || InformationModel.GetObject(cfg_dataStoreId) == null)
            {
                if (meterReadingDataStore != null) meterReadingDataLogger.Store = meterReadingDataStore.NodeId;
                Log.Info("Fiix Library initializing", "assigning DataStore " + meterReadingDataStore.BrowseName + " to MeterReadingDataLogger");
            }
        }

        // Assign model to Asset faceplate meter reading trend model, to avoid runtime Trend source error
        PanelType meterReadingTrendPanel = (PanelType)Project.Current.Find("raI_FiixAsset_1_00_MeterReadingTrend");
        if (meterReadingDataStore != null && meterReadingTrendPanel != null)
        {
            Trend meterReadingTrend = (Trend)meterReadingTrendPanel.Find("Trend1");
            if (meterReadingTrend != null) meterReadingTrend.Model = meterReadingDataStore.NodeId;
        }

        // Sync Fiix configuration using API client
        CmmsApiConnectionConfiguration config = GatewayUtils.GetFiix_APIConnectionConfiguration();
        using (var cmmsHttpClientHandlerInstance = CmmsHttpClientHandler.Create(config, false))
        {
            var apiServiceInstance = new CmmsApiService(cmmsHttpClientHandlerInstance);

            SyncAssetCategory(apiServiceInstance);
            SyncAssetEventType(apiServiceInstance);
            SyncMeterReadingUnit(apiServiceInstance);
            SyncOfflineReason(apiServiceInstance);
            SyncOnlineReason(apiServiceInstance);
            // Added Sep 2024, for v1.2 Work Order creation and history query function
            SyncPriority(apiServiceInstance);
            SyncWorkOrderStatus(apiServiceInstance);
            SyncMaintenanceType(apiServiceInstance);

            Log.Info("Fiix Gateway", LogicObject.GetVariable("Sts_LastExecutionResult").Value);
        }
    }

    [ExportMethod]
    public void SyncAssets()
    {
        GatewayUtils.SyncAssetTree(true);
        Log.Info("Fiix Gateway", LogicObject.GetVariable("Sts_LastExecutionResult").Value);
    }

    private void SyncAssetCategory(CmmsApiService apiServiceInstance)
    {
        // Sync AssetCategory, exclude System Category
        ApiResponseModel responseModel = apiServiceInstance.FindAssetCategories().Result;
        LogicObject.GetVariable("Sts_LastExecutionDatetime").Value = DateTime.Now;
        if (!responseModel.Success )
        {
            LogicObject.GetVariable("Sts_LastExecutionResult").Value = "Get Fiix AssetCategories with error.";
            if (!responseModel.Success) Log.Error("Fiix Gateway", "Sync Asset Category error: " + responseModel.ErrorMessage);
            return;
        }
        Fiix_AssetCategory[] fiixAssetCategories = responseModel.objects.OfType<Fiix_AssetCategory>().ToArray();
        if (fiixAssetCategories == null || fiixAssetCategories.Length == 0)
        {
            LogicObject.GetVariable("Sts_LastExecutionResult").Value = "Get Fiix AssetCategories with no result.";
            if (!responseModel.Success) Log.Error("Fiix Gateway", "Sync Asset Category error: " + responseModel.ErrorMessage);
            return;
        }
        List<IUANode> assetCategories = categoryFolder.Children.Cast<IUANode>().ToList();

        // Delete extra nodes if enabled
        if ((bool)LogicObject.GetVariable("Set_DeleteExtraNodes").Value)
        {
            foreach (IUANode assetCategory in assetCategories)
            {
                if (!Array.Exists(fiixAssetCategories, fiixCategory => fiixCategory.id == (int)assetCategory.GetVariable("id").Value || fiixCategory.strName == (string)assetCategory.GetVariable("strName").Value))
                {
                    assetCategory.Delete();
                }
            }
        }

        foreach (var fiixAssetCategory in fiixAssetCategories)
        {
            IUANode newCategory;

            // Include SysCode that equal -1 only which is the default value when no value return from API (in the case of System Categories)
            // Updated to include System Categories by comment out the filtering below.
            //if (fiixAssetCategory.intSysCode != -1) continue; 
            if (!assetCategories.Exists(category => fiixAssetCategory.id == (int)category.GetVariable("id").Value))
            {
                newCategory = InformationModel.MakeObject(fiixAssetCategory.strName, AssetCategoryType.NodeId);
                newCategory.GetVariable("id").Value = fiixAssetCategory.id;
                newCategory.GetVariable("strName").Value = fiixAssetCategory.strName;
                newCategory.GetVariable("strUuid").Value = fiixAssetCategory.strUuid;
                newCategory.GetVariable("intSysCode").Value = fiixAssetCategory.intSysCode;
                newCategory.GetVariable("intParentID").Value = fiixAssetCategory.intParentID;
                newCategory.GetVariable("Cfg_enabled").Value = false;

                newCategoryCount++;
                categoryFolder.Add(newCategory);

                // Sort by name
                var updateds = categoryFolder.Children.Cast<IUANode>().ToList();
                var cpCount = assetCategories.Count();
                for (int i = cpCount - 1; i >= 0; i--)
                {
                    try
                    {
                        if (string.Compare(updateds[i].BrowseName, newCategory.BrowseName) > 0) newCategory.MoveUp();
                    }
                    catch { Log.Info("error when sort AssetCategories"); }
                }
            }
            else
            {
                newCategory = assetCategories.Find(site => fiixAssetCategory.id == site.GetVariable("id").Value);
                newCategory.BrowseName = fiixAssetCategory.strName;
                newCategory.GetVariable("strName").Value = fiixAssetCategory.strName;
                newCategory.GetVariable("strUuid").Value = fiixAssetCategory.strUuid;
                newCategory.GetVariable("intSysCode").Value = fiixAssetCategory.intSysCode;
                newCategory.GetVariable("intParentID").Value = fiixAssetCategory.intParentID;
                updateCategoryCount++;
            }
        }
        LogicObject.GetVariable("Sts_LastExecutionResult").Value = newCategoryCount + " new and " + updateCategoryCount + " synced AssetCategory; ";
    }

    private void SyncAssetEventType(CmmsApiService apiServiceInstance)
    {
        // Sync AssetEventType
        ApiResponseModel responseModel = apiServiceInstance.FindAssetEventTypes().Result;
        if (!responseModel.Success )
        {
            LogicObject.GetVariable("Sts_LastExecutionResult").Value += "Get Fiix AssetEventTypes with error.";
            if (!responseModel.Success) Log.Error("Fiix Gateway", "Sync Asset EventType error: " + responseModel.ErrorMessage);
            return;
        }
        Fiix_AssetEventType[] fiixAssetEventTypes = responseModel.objects.OfType<Fiix_AssetEventType>().ToArray();
        if (fiixAssetEventTypes == null || fiixAssetEventTypes.Length == 0)
        {
            LogicObject.GetVariable("Sts_LastExecutionResult").Value += "Get Fiix AssetEventTypes with no result.";
            if (!responseModel.Success) Log.Error("Fiix Gateway", "Sync Asset EventType error: " + responseModel.ErrorMessage);
            return;
        }
        List<IUANode> assetEventTypes = eventTypeFolder.Children.Cast<IUANode>().ToList();

        // Delete extra nodes if enabled
        if ((bool)LogicObject.GetVariable("Set_DeleteExtraNodes").Value)
        {
            foreach (IUANode assetEventType in assetEventTypes)
            {
                if (!Array.Exists(fiixAssetEventTypes, fiixEventType => fiixEventType.id == (int)assetEventType.GetVariable("id").Value || fiixEventType.strEventCode == (string)assetEventType.GetVariable("strEventCode").Value))
                {
                    assetEventType.Delete();
                }
            }
        }

        foreach (var fiixAssetEventType in fiixAssetEventTypes)
        {
            IUANode newEventType;

            if (!assetEventTypes.Exists(eventType => fiixAssetEventType.id == (int)eventType.GetVariable("id").Value))
            {
                newEventType = InformationModel.MakeObject(fiixAssetEventType.strEventCode + " - " + fiixAssetEventType.strEventName, AssetEventTypeType.NodeId);
                newEventType.GetVariable("id").Value = fiixAssetEventType.id;
                newEventType.GetVariable("strEventName").Value = fiixAssetEventType.strEventName;
                newEventType.GetVariable("strUniqueKey").Value = fiixAssetEventType.strUniqueKey;
                newEventType.GetVariable("strEventCode").Value = fiixAssetEventType.strEventCode;
                newEventType.GetVariable("strEventDescription").Value = fiixAssetEventType.strEventDescription;

                newEventTypeCount++;
                eventTypeFolder.Add(newEventType);

                // Sort by name
                var updateds = eventTypeFolder.Children.Cast<IUANode>().ToList();
                var cpCount = assetEventTypes.Count();
                for (int i = cpCount - 1; i >= 0; i--)
                {
                    try
                    {
                        if (string.Compare(updateds[i].BrowseName, newEventType.BrowseName) > 0) newEventType.MoveUp();
                    }
                    catch { Log.Info("error when sort AssetEventTypes"); }
                }
            }
            else
            {
                newEventType = assetEventTypes.Find(site => fiixAssetEventType.id == site.GetVariable("id").Value);
                newEventType.BrowseName = fiixAssetEventType.strEventCode + " - " + fiixAssetEventType.strEventName;
                newEventType.GetVariable("strEventName").Value = fiixAssetEventType.strEventName;
                newEventType.GetVariable("strUniqueKey").Value = fiixAssetEventType.strUniqueKey;
                newEventType.GetVariable("strEventCode").Value = fiixAssetEventType.strEventCode;
                newEventType.GetVariable("strEventDescription").Value = fiixAssetEventType.strEventDescription;
                updateEventTypeCount++;
            }
        }
        LogicObject.GetVariable("Sts_LastExecutionResult").Value += newEventTypeCount + " new and " + updateEventTypeCount + " synced EventType; ";
    }

    private void SyncMeterReadingUnit(CmmsApiService apiServiceInstance)
    {
    // Sync MeterReadingUnit
        ApiResponseModel responseModel = apiServiceInstance.FindMeterReadingUnits().Result;

        if (!responseModel.Success )
        {
            LogicObject.GetVariable("Sts_LastExecutionResult").Value += "Get Fiix MeterReadingUnits with error.";
            if (!responseModel.Success) Log.Error("Fiix Gateway", "Sync MeterReadingUnits error: " + responseModel.ErrorMessage);
            return;
        }
        Fiix_MeterReadingUnit[] fiixMeterReadingUnits = responseModel.objects.OfType<Fiix_MeterReadingUnit>().ToArray();
        if (fiixMeterReadingUnits == null || fiixMeterReadingUnits.Length == 0)
        {
            LogicObject.GetVariable("Sts_LastExecutionResult").Value += "Get Fiix MeterReadingUnits with no result.";
            if (!responseModel.Success) Log.Error("Fiix Gateway", "Sync MeterReadingUnits error: " + responseModel.ErrorMessage);
            return;
        }
        var arrayDimensions = new uint[1];
        arrayDimensions[0] = (uint)fiixMeterReadingUnits.Length;

        IUAVariable meterReadingEngineeringUnitDictionary;
        if (LogicObject.Owner.Owner.Find("MeterReadingUnits") != null)
        {
            meterReadingEngineeringUnitDictionary = (IUAVariable)LogicObject.Owner.Owner.Find("MeterReadingUnits");
        }
        else
        {
            meterReadingEngineeringUnitDictionary = InformationModel.MakeVariable("MeterReadingUnits", FTOptix.Core.DataTypes.EngineeringUnitDictionaryItem, FTOptix.Core.VariableTypes.EngineeringUnitDictionary, arrayDimensions);
            LogicObject.Owner.Owner.Add(meterReadingEngineeringUnitDictionary);
        }

        EngineeringUnitDictionaryItem[] newItems = new EngineeringUnitDictionaryItem[fiixMeterReadingUnits.Length];

        for (int i = 0; i < fiixMeterReadingUnits.Length; i++)
        {
            EngineeringUnitDictionaryItem newItem = new EngineeringUnitDictionaryItem();
            newItem.PhysicalDimension = PhysicalDimension.None;
            newItem.Slope = 0;
            newItem.Description = new LocalizedText("Fiix_" + fiixMeterReadingUnits[i].strName, Session.ActualLocaleId);
            newItem.UnitId = fiixMeterReadingUnits[i].id;
            newItem.Intercept = 0;
            newItem.DisplayName = new LocalizedText(fiixMeterReadingUnits[i].strSymbol, Session.ActualLocaleId);
            //newItem.DisplayName = new LocalizedText(fiixMeterReadingUnits[i].strSymbol, "en-US");
            newItems[i] = newItem;
        }
        meterReadingEngineeringUnitDictionary.Value = newItems;
    }

    private void SyncOnlineReason(CmmsApiService apiServiceInstance)
    {
        ApiResponseModel responseModel = apiServiceInstance.FindReasonToSetAssetOnline().Result;
        if (!responseModel.Success )
        {
            LogicObject.GetVariable("Sts_LastExecutionResult").Value += "Get Fiix OnlineReason with error.";
            if (!responseModel.Success) Log.Error("Fiix Gateway", "Sync Online Reasons error: " + responseModel.ErrorMessage);
            return;
        }
        Fiix_ReasonToSetAssetOnline[] fiix_AssetOnlineReasons = responseModel.objects.OfType<Fiix_ReasonToSetAssetOnline>().ToArray();
        if (fiix_AssetOnlineReasons == null || fiix_AssetOnlineReasons.Length == 0)
        {
            LogicObject.GetVariable("Sts_LastExecutionResult").Value += "Get Fiix OnlineReason with no result.";
            if (!responseModel.Success) Log.Error("Fiix Gateway", "Sync Online Reasons error: " + responseModel.ErrorMessage);
            return;
        }
        List<IUANode> assetOnlineReasons = assetOnlineReasonFolder.Children.Cast<IUANode>().ToList();

        // Delete extra nodes if enabled
        if ((bool)LogicObject.GetVariable("Set_DeleteExtraNodes").Value)
        {
            foreach (IUANode assetOnlineReason in assetOnlineReasons)
            {
                if (!Array.Exists(fiix_AssetOnlineReasons, fiixOnlineReason => fiixOnlineReason.id == (int)assetOnlineReason.GetVariable("id").Value))
                {
                    assetOnlineReason.Delete();
                }
            }
        }

        foreach (var fiixAssetOnlineReason in fiix_AssetOnlineReasons)
        {
            IUANode newOnlineReason;

            if (!assetOnlineReasons.Exists(OnlineReason => fiixAssetOnlineReason.id == (int)OnlineReason.GetVariable("id").Value))
            {
                newOnlineReason = InformationModel.MakeObject(fiixAssetOnlineReason.strName, AssetOfflineReasonType.NodeId);
                newOnlineReason.GetVariable("id").Value = fiixAssetOnlineReason.id;
                newOnlineReason.GetVariable("strName").Value = fiixAssetOnlineReason.strName;
                newOnlineReason.GetVariable("dtUpdated").Value = DateTimeOffset.FromUnixTimeMilliseconds(fiixAssetOnlineReason.intUpdated).DateTime;
                newOnlineReason.GetVariable("strUuid").Value = fiixAssetOnlineReason.strUuid;

                newOnlineReasonCount++;
                assetOnlineReasonFolder.Add(newOnlineReason);

                // Sort by name
                var updateds = assetOnlineReasonFolder.Children.Cast<IUANode>().ToList();
                var cpCount = assetOnlineReasons.Count();
                for (int i = cpCount - 1; i >= 0; i--)
                {
                    try
                    {
                        if (string.Compare(updateds[i].BrowseName, newOnlineReason.BrowseName) > 0) newOnlineReason.MoveUp();
                    }
                    catch { Log.Info("error when sort AssetOnlineReasons"); }
                }
            }
            else
            {
                newOnlineReason = assetOnlineReasons.Find(site => fiixAssetOnlineReason.id == site.GetVariable("id").Value);
                newOnlineReason.BrowseName = fiixAssetOnlineReason.strName;
                newOnlineReason.GetVariable("strName").Value = fiixAssetOnlineReason.strName;
                newOnlineReason.GetVariable("id").Value = fiixAssetOnlineReason.id;
                newOnlineReason.GetVariable("dtUpdated").Value = DateTimeOffset.FromUnixTimeMilliseconds(fiixAssetOnlineReason.intUpdated).DateTime;
                newOnlineReason.GetVariable("strUuid").Value = fiixAssetOnlineReason.strUuid;
                updateOnlineReasonCount++;
            }
        }

        LogicObject.GetVariable("Sts_LastExecutionResult").Value += newOnlineReasonCount + " new and " + updateOnlineReasonCount + " synced OnlineReason; ";
    }

    private void SyncOfflineReason(CmmsApiService apiServiceInstance)
    {
        ApiResponseModel responseModel = apiServiceInstance.FindReasonToSetAssetOffline().Result;

        if (!responseModel.Success )
        {
            LogicObject.GetVariable("Sts_LastExecutionResult").Value += "Get Fiix OfflineReason with error.";
            if (!responseModel.Success) Log.Error("Fiix Gateway", "Sync Offline Reasons error: " + responseModel.ErrorMessage);
            return;
        }
        Fiix_ReasonToSetAssetOffline[] fiix_AssetOfflineReasons = responseModel.objects.OfType<Fiix_ReasonToSetAssetOffline>().ToArray();
        if (fiix_AssetOfflineReasons == null || fiix_AssetOfflineReasons.Length == 0)
        {
            LogicObject.GetVariable("Sts_LastExecutionResult").Value += "Get Fiix OfflineReason with no result.";
            if (!responseModel.Success) Log.Error("Fiix Gateway", "Sync Offline Reasons error: " + responseModel.ErrorMessage);
            return;
        }
        List<IUANode> assetOfflineReasons = assetOfflineReasonFolder.Children.Cast<IUANode>().ToList();

        // Delete extra nodes if enabled
        if ((bool)LogicObject.GetVariable("Set_DeleteExtraNodes").Value)
        {
            foreach (IUANode assetOfflineReason in assetOfflineReasons)
            {
                if (!Array.Exists(fiix_AssetOfflineReasons, fiixOfflineReason => fiixOfflineReason.id == (int)assetOfflineReason.GetVariable("id").Value))
                {
                    assetOfflineReason.Delete();
                }
            }
        }

        foreach (var fiixAssetOfflineReason in fiix_AssetOfflineReasons)
        {
            IUANode newOfflineReason;

            if (!assetOfflineReasons.Exists(OfflineReason => fiixAssetOfflineReason.id == (int)OfflineReason.GetVariable("id").Value))
            {
                newOfflineReason = InformationModel.MakeObject(fiixAssetOfflineReason.strName, AssetOfflineReasonType.NodeId);
                newOfflineReason.GetVariable("id").Value = fiixAssetOfflineReason.id;
                newOfflineReason.GetVariable("strName").Value = fiixAssetOfflineReason.strName;
                newOfflineReason.GetVariable("dtUpdated").Value = DateTimeOffset.FromUnixTimeMilliseconds(fiixAssetOfflineReason.intUpdated).DateTime;
                newOfflineReason.GetVariable("strUuid").Value = fiixAssetOfflineReason.strUuid;

                newOfflineReasonCount++;
                assetOfflineReasonFolder.Add(newOfflineReason);

                // Sort by name
                var updateds = assetOfflineReasonFolder.Children.Cast<IUANode>().ToList();
                var cpCount = assetOfflineReasons.Count();
                for (int i = cpCount - 1; i >= 0; i--)
                {
                    try
                    {
                        if (string.Compare(updateds[i].BrowseName, newOfflineReason.BrowseName) > 0) newOfflineReason.MoveUp();
                    }
                    catch { Log.Info("error when sort AssetOfflineReasons"); }
                }
            }
            else
            {
                newOfflineReason = assetOfflineReasons.Find(site => fiixAssetOfflineReason.id == site.GetVariable("id").Value);
                newOfflineReason.BrowseName = fiixAssetOfflineReason.strName;
                newOfflineReason.GetVariable("strName").Value = fiixAssetOfflineReason.strName;
                newOfflineReason.GetVariable("id").Value = fiixAssetOfflineReason.id;
                newOfflineReason.GetVariable("dtUpdated").Value = DateTimeOffset.FromUnixTimeMilliseconds(fiixAssetOfflineReason.intUpdated).DateTime;
                newOfflineReason.GetVariable("strUuid").Value = fiixAssetOfflineReason.strUuid;
                updateOfflineReasonCount++;
            }
        }

        LogicObject.GetVariable("Sts_LastExecutionResult").Value += newOfflineReasonCount + " new and " + updateOfflineReasonCount + " synced OfflineReason; ";

    }

    private void SyncPriority(CmmsApiService apiServiceInstance)
    {
        ApiResponseModel responseModel = apiServiceInstance.FindPriorities().Result;

        if (!responseModel.Success )
        {
            LogicObject.GetVariable("Sts_LastExecutionResult").Value += "Get Fiix Priority with error.";
            if (!responseModel.Success) Log.Error("Fiix Gateway", "Sync Priority error: " + responseModel.ErrorMessage);
            return;
        }
        Fiix_Priority[] fiix_Priorities = responseModel.objects.OfType<Fiix_Priority>().ToArray();
        if (fiix_Priorities == null || fiix_Priorities.Length == 0)
        {
            LogicObject.GetVariable("Sts_LastExecutionResult").Value += "Get Fiix Priority with no result.";
            if (!responseModel.Success) Log.Error("Fiix Gateway", "Sync Priority error: " + responseModel.ErrorMessage);
            return;
        }
        List<IUANode> priorities = priorityFolder.Children.Cast<IUANode>().ToList();

        // Delete extra nodes if enabled
        if ((bool)LogicObject.GetVariable("Set_DeleteExtraNodes").Value)
        {
            foreach (IUANode priority in priorities)
            {
                if (!Array.Exists(fiix_Priorities, item => item.id == (int)priority.GetVariable("id").Value))
                {
                    priority.Delete();
                }
            }
        }

        foreach (var fiixPriority in fiix_Priorities)
        {
            IUANode newPriority;

            if (!priorities.Exists(Priority => fiixPriority.id == (int)Priority.GetVariable("id").Value))
            {
                newPriority = InformationModel.MakeObject(fiixPriority.strName, PriorityType.NodeId);
                newPriority.GetVariable("id").Value = fiixPriority.id;
                newPriority.GetVariable("strName").Value = fiixPriority.strName;
                newPriority.GetVariable("intOrder").Value = fiixPriority.intOrder;
                newPriority.GetVariable("strUuid").Value = fiixPriority.strUuid;
                newPriority.GetVariable("intSysCode").Value = fiixPriority.intSysCode;

                newPriorityCount++;
                priorityFolder.Add(newPriority);

                // Sort by name
                var updateds = priorityFolder.Children.Cast<IUANode>().ToList();
                var cpCount = priorities.Count();
                for (int i = cpCount - 1; i >= 0; i--)
                {
                    try
                    {
                        if (string.Compare(updateds[i].BrowseName, newPriority.BrowseName) > 0) newPriority.MoveUp();
                    }
                    catch { Log.Info("error when sort Priority"); }
                }
            }
            else
            {
                newPriority = priorities.Find(site => fiixPriority.id == site.GetVariable("id").Value);
                newPriority.BrowseName = fiixPriority.strName;
                newPriority.GetVariable("strName").Value = fiixPriority.strName;
                newPriority.GetVariable("intOrder").Value = fiixPriority.intOrder;
                newPriority.GetVariable("strUuid").Value = fiixPriority.strUuid;
                newPriority.GetVariable("intSysCode").Value = fiixPriority.intSysCode;
                updatePriorityCount++;
            }
        }

        LogicObject.GetVariable("Sts_LastExecutionResult").Value += newPriorityCount + " new and " + updatePriorityCount + " synced Priority; ";
    }

    private void SyncWorkOrderStatus(CmmsApiService apiServiceInstance)
    {
        ApiResponseModel responseModel = apiServiceInstance.FindWorkOrderStatus().Result;

        if (!responseModel.Success )
        {
            LogicObject.GetVariable("Sts_LastExecutionResult").Value += "Get Fiix WorkOrderStatus with error.";
            if (!responseModel.Success) Log.Error("Fiix Gateway", "Sync WorkOrderStatus error: " + responseModel.ErrorMessage);
            return;
        }
        Fiix_WorkOrderStatus[] fiix_WorkOrderStatus = responseModel.objects.OfType<Fiix_WorkOrderStatus>().ToArray();
        if (fiix_WorkOrderStatus == null || fiix_WorkOrderStatus.Length == 0)
        {
            LogicObject.GetVariable("Sts_LastExecutionResult").Value += "Get Fiix WorkOrderStatus with no result.";
            if (!responseModel.Success) Log.Error("Fiix Gateway", "Sync WorkOrderStatus error: " + responseModel.ErrorMessage);
            return;
        }
        List<IUANode> woStatusList = woStatusFolder.Children.Cast<IUANode>().ToList();

        // Delete extra nodes if enabled
        if ((bool)LogicObject.GetVariable("Set_DeleteExtraNodes").Value)
        {
            foreach (IUANode workOrderStatus in woStatusList)
            {
                if (!Array.Exists(fiix_WorkOrderStatus, item => item.id == (int)workOrderStatus.GetVariable("id").Value))
                {
                    workOrderStatus.Delete();
                }
            }
        }

        foreach (var fiixWorkOrderStatus in fiix_WorkOrderStatus)
        {
            IUANode newWorkOrderStatus;

            if (!woStatusList.Exists(WorkOrderStatus => fiixWorkOrderStatus.id == (int)WorkOrderStatus.GetVariable("id").Value))
            {
                newWorkOrderStatus = InformationModel.MakeObject(fiixWorkOrderStatus.strName, WOStatusType.NodeId);
                newWorkOrderStatus.GetVariable("id").Value = fiixWorkOrderStatus.id;
                newWorkOrderStatus.GetVariable("strName").Value = fiixWorkOrderStatus.strName;
                newWorkOrderStatus.GetVariable("intControlID").Value = fiixWorkOrderStatus.intControlID;
                newWorkOrderStatus.GetVariable("strUuid").Value = fiixWorkOrderStatus.strUuid;
                newWorkOrderStatus.GetVariable("intSysCode").Value = fiixWorkOrderStatus.intSysCode;

                newWOStatusCount++;
                woStatusFolder.Add(newWorkOrderStatus);

                // Sort by name
                var updateds = woStatusFolder.Children.Cast<IUANode>().ToList();
                var cpCount = woStatusList.Count();
                for (int i = cpCount - 1; i >= 0; i--)
                {
                    try
                    {
                        if (string.Compare(updateds[i].BrowseName, newWorkOrderStatus.BrowseName) > 0) newWorkOrderStatus.MoveUp();
                    }
                    catch { Log.Info("error when sort WorkOrderStatus"); }
                }
            }
            else
            {
                newWorkOrderStatus = woStatusList.Find(site => fiixWorkOrderStatus.id == site.GetVariable("id").Value);
                newWorkOrderStatus.BrowseName = fiixWorkOrderStatus.strName;
                newWorkOrderStatus.GetVariable("strName").Value = fiixWorkOrderStatus.strName;
                newWorkOrderStatus.GetVariable("intControlID").Value = fiixWorkOrderStatus.intControlID;
                newWorkOrderStatus.GetVariable("strUuid").Value = fiixWorkOrderStatus.strUuid;
                newWorkOrderStatus.GetVariable("intSysCode").Value = fiixWorkOrderStatus.intSysCode;
                updateWOStatusCount++;
            }
        }

        LogicObject.GetVariable("Sts_LastExecutionResult").Value += newWOStatusCount + " new and " + updateWOStatusCount + " synced WorkOrderStatus; ";
    }

    private void SyncMaintenanceType(CmmsApiService apiServiceInstance)
    {
        ApiResponseModel responseModel = apiServiceInstance.FindMaintenanceTypes().Result;

        if (!responseModel.Success )
        {
            LogicObject.GetVariable("Sts_LastExecutionResult").Value += "Get Fiix MaintenanceType with error.";
            if (!responseModel.Success) Log.Error("Fiix Gateway", "Sync MaintenanceType error: " + responseModel.ErrorMessage);
            return;
        }
        Fiix_MaintenanceType[] fiix_MaintenanceTypes = responseModel.objects.OfType<Fiix_MaintenanceType>().ToArray();
        if (fiix_MaintenanceTypes == null || fiix_MaintenanceTypes.Length == 0)
        {
            LogicObject.GetVariable("Sts_LastExecutionResult").Value += "Get Fiix MaintenanceType with no result.";
            if (!responseModel.Success) Log.Error("Fiix Gateway", "Sync MaintenanceType error: " + responseModel.ErrorMessage);
            return;
        }
        List<IUANode> maintenanceTypes = maintenanceTypeFolder.Children.Cast<IUANode>().ToList();

        // Delete extra nodes if enabled
        if ((bool)LogicObject.GetVariable("Set_DeleteExtraNodes").Value)
        {
            foreach (IUANode maintenanceType in maintenanceTypes)
            {
                if (!Array.Exists(fiix_MaintenanceTypes, item => item.id == (int)maintenanceType.GetVariable("id").Value))
                {
                    maintenanceType.Delete();
                }
            }
        }

        foreach (var fiixMaintenanceType in fiix_MaintenanceTypes)
        {
            IUANode newMaintenanceType;

            if (!maintenanceTypes.Exists(MaintenanceType => fiixMaintenanceType.id == (int)MaintenanceType.GetVariable("id").Value))
            {
                newMaintenanceType = InformationModel.MakeObject(fiixMaintenanceType.strName, MaintenanceTypeType.NodeId);
                newMaintenanceType.GetVariable("id").Value = fiixMaintenanceType.id;
                newMaintenanceType.GetVariable("strName").Value = fiixMaintenanceType.strName;
                newMaintenanceType.GetVariable("strColor").Value = fiixMaintenanceType.strColor;
                newMaintenanceType.GetVariable("strUuid").Value = fiixMaintenanceType.strUuid;
                newMaintenanceType.GetVariable("intSysCode").Value = fiixMaintenanceType.intSysCode;

                newMaintenanceTypeCount++;
                maintenanceTypeFolder.Add(newMaintenanceType);

                // Sort by name
                var updateds = maintenanceTypeFolder.Children.Cast<IUANode>().ToList();
                var cpCount = maintenanceTypes.Count();
                for (int i = cpCount - 1; i >= 0; i--)
                {
                    try
                    {
                        if (string.Compare(updateds[i].BrowseName, newMaintenanceType.BrowseName) > 0) newMaintenanceType.MoveUp();
                    }
                    catch { Log.Info("error when sort MaintenanceType"); }
                }
            }
            else
            {
                newMaintenanceType = maintenanceTypes.Find(site => fiixMaintenanceType.id == site.GetVariable("id").Value);
                newMaintenanceType.BrowseName = fiixMaintenanceType.strName;
                newMaintenanceType.GetVariable("strName").Value = fiixMaintenanceType.strName;
                newMaintenanceType.GetVariable("id").Value = fiixMaintenanceType.id;
                newMaintenanceType.GetVariable("dtUpdated").Value = DateTimeOffset.FromUnixTimeMilliseconds(fiixMaintenanceType.intUpdated).DateTime;
                newMaintenanceType.GetVariable("strUuid").Value = fiixMaintenanceType.strUuid;
                updateMaintenanceTypeCount++;
            }
        }

        LogicObject.GetVariable("Sts_LastExecutionResult").Value += newMaintenanceTypeCount + " new and " + updateMaintenanceTypeCount + " synced MaintenanceType; ";
    }

    IUANode categoryFolder;
    IUANode AssetCategoryType;
    IUANode eventTypeFolder;
    IUANode AssetEventTypeType ;
    IUANode assetOfflineReasonFolder;
    IUANode assetOnlineReasonFolder;
    IUANode AssetOfflineReasonType;
    IUANode priorityFolder ;
    IUANode PriorityType, woStatusFolder, WOStatusType, maintenanceTypeFolder, MaintenanceTypeType;
}

