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
using FTOptix.OPCUAClient;
using System.Linq;
using FTOptix.WebUI;
using FTOptix.EventLogger;
using System.Collections.Generic;
using Gpe.Integration.Fiix.Connector;
using Gpe.Integration.Fiix.Connector.Models;
using Gpe.Integration.Fiix.Connector.Services;
using Gpe.Integration.Fiix.Connector.Models.Utilities;
#endregion
/*
Fiix Gateway runtime script to provide actions on Asset.
=============================================================

Disclaimer of Warranty
THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT ARE PROVIDED "AS IS" WITHOUT WARRANTIES OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION, ALL IMPLIED WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE, NON-INFRINGEMENT OR OTHER VIOLATION OF RIGHTS. ROCKWELL AUTOMATION DOES NOT WARRANT OR MAKE ANY REPRESENTATIONS REGARDING THE USE, VALIDITY, ACCURACY, OR RELIABILITY OF, OR THE RESULTS OF ANY USE OF, OR OTHERWISE RESPECTING, THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT OR ANY WEB SITE LINKED TO THIS DOCUMENT 

Limitation of Liability
UNDER NO CIRCUMSTANCE (INCLUDING NEGLIGENCE AND TO THE FULLEST EXTEND PERMITTED BY APPLICABLE LAW) WILL ROCKWELL AUTOMATION BE LIABLE FOR ANY DIRECT, INDIRECT, SPECIAL, INCIDENTAL, PUNITIVE OR CONSEQUENTIAL DAMAGES (INCLUDING WITHOUT LIMITATION, BUSINESS INTERRUPTION, DELAYS, LOSS OF DATA OR PROFIT) ARISING OUT OF THE USE OR THE INABILITY TO USE THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT EVEN IF ROCKWELL AUTOMATION HAS BEEN ADVISED OF THE POSSIBILITY OF SUCH DAMAGES. IF USE OF SUCH MATERIALS RESULTS IN THE NEED FOR SERVICING, REPAIR OR CORRECTION OF USER EQUIPMENT OR DATA, USER ASSUMES ANY COSTS ASSOCIATED THEREWITH.

Copyright © Rockwell Automation, Inc.  All Rights Reserved. 

=============================================================
*/
[CustomBehavior]
public class AssetBehavior : BaseNetBehavior
{
    public override void Start()
    {
        // Insert code to be executed when the user-defined behavior is started
    }

    public override void Stop()
    {
        // Insert code to be executed when the user-defined behavior is stopped
    }

    [ExportMethod]
    public void SwitchOnline()
    {
        CmmsApiService apiSingletonService = GatewayUtils.GetFiixCMMS_SingletonAPIService();
        ApiResponseModel response = apiSingletonService.ChangeAssetOnlineStatus(this.Node.id, 1).Result;
        if (response.Success ) this.Node.Sts_LastActionResult = "Switch Asset " + Node.strName + " Online succeeded.";
        else
        {
            this.Node.Sts_LastActionResult = "Switch Asset " + Node.strName + " Online failed. " ;
            Log.Error("Switch Asset" + Node.strName + " online error: " + response.ErrorMessage );
        }
    }

    [ExportMethod]
    public void SwitchOffline()
    {
        CmmsApiService apiSingletonService = GatewayUtils.GetFiixCMMS_SingletonAPIService();
        var response = apiSingletonService.ChangeAssetOnlineStatus(this.Node.id, 0).Result;
        if (response.Success)
        {
            this.Node.Sts_LastActionResult = "Switch Asset " + Node.strName + " Offline succeeded.";
        }
        else
        {
            this.Node.Sts_LastActionResult = "Switch Asset " + Node.strName + " Offline failed. ";
            Log.Error("Switch Asset" + Node.strName + " offline error: " + response.ErrorMessage);
        }
        this.Node.Sts_LastActionDatetime = DateTime.Now;
    }

    // Get latest properties values from Fiix for this Asset
    [ExportMethod]
    public void UpdateRuntimeAsset() {
        CmmsApiService apiSingletonService = GatewayUtils.GetFiixCMMS_SingletonAPIService();
        int assetID = (int)this.Node.GetVariable("id").Value;
        List<Fiix_WorkOrder> openWorkOrderList = new List<Fiix_WorkOrder>();

        // Get open Work Order involved with the asset,
        ApiResponseModel responseWO = null;
        IUANode woStatusFolder = Project.Current.Find<Folder>("WorkOrderStatus");
        List<WOStatus> statusList = woStatusFolder.Children.Cast<WOStatus>().ToList();

        // Find all Work Order Status whose ControlID is not 102 (designated by Fiix API for Closed)
        List<WOStatus> nonClosedWOStatusList = statusList.FindAll(x => int.TryParse(x.GetVariable("intControlID").Value, out int a) && (int)x.GetVariable("intControlID").Value != 102);

        ApiResponseModel response = apiSingletonService.FindAssetByID(assetID).Result;

        if (nonClosedWOStatusList.Count != 0)
        {
            foreach (WOStatus wos in nonClosedWOStatusList)
            {
                if (wos==null || wos.Context == null ) continue;
                responseWO = apiSingletonService.FindWorkOrderByAssetIDAndTimeRange(assetID.ToString(), null, null, wos.id).Result;
                if (responseWO.Success)
                {
                    foreach (Fiix_WorkOrder wo in responseWO.objects)
                    {
                        openWorkOrderList.Add(wo);
                    }
                }
                else
                {
                    Log.Error("Fiix Gateway", "Get work orders with status " + wos.BrowseName + " for asset " + this.Node.BrowseName + " error: " + responseWO.ErrorMessage);
                }
            }
        }
        else Log.Info("Fiix Gateway", "Get asset " + this.Node.BrowseName + " open work order error, cannot get Open status list.");

        if (response.Success && response.objects!=null && response.objects.Length > 0)
        {
            Fiix_Asset asset = (Fiix_Asset)response.objects[0];
            this.Node.GetVariable("id").Value = asset.id;
            this.Node.GetVariable("strName").Value = asset.strName;
            this.Node.GetVariable("strCode").Value = asset.strCode;
            this.Node.GetVariable("strAddressParsed").Value = asset.strAddressParsed;
            this.Node.GetVariable("strTimezone").Value = asset.strTimezone;
            this.Node.GetVariable("intAssetLocationID").Value = asset.intAssetLocationID;
            this.Node.GetVariable("intCategoryID").Value = asset.intCategoryID;
            this.Node.GetVariable("intSiteID").Value = asset.intSiteID;
            this.Node.GetVariable("intSuperCategorySysCode").Value = asset.intSuperCategorySysCode;
            this.Node.GetVariable("strBinNumber").Value = asset.strBinNumber;
            this.Node.GetVariable("strRow").Value = asset.strRow;
            this.Node.GetVariable("strAisle").Value = asset.strAisle;
            this.Node.GetVariable("strDescription").Value = asset.strDescription;
            this.Node.GetVariable("strInventoryCode").Value = asset.strInventoryCode;
            this.Node.GetVariable("strMake").Value = asset.strMake;
            this.Node.GetVariable("strModel").Value = asset.strModel;
            this.Node.GetVariable("strSerialNumber").Value = asset.strSerialNumber;
            this.Node.GetVariable("bolIsOnline").Value = Convert.ToBoolean(asset.bolIsOnline);
            this.Node.GetVariable("bolIsSite").Value = Convert.ToBoolean(asset.bolIsSite);
            this.Node.GetVariable("bolIsRegion").Value = Convert.ToBoolean(asset.bolIsRegion);
            this.Node.GetVariable("dtUpdated").Value = DateTimeOffset.FromUnixTimeMilliseconds(asset.intUpdated).DateTime;
            this.Node.Sts_LastActionResult = "Get asset " + Node.strName + " data succeeded.";
            if (nonClosedWOStatusList.Count != 0 ) this.Node.GetVariable("Sts_OpenWorkOrderCount").Value = openWorkOrderList.Count;
        }
        else
        {
            this.Node.Sts_LastActionResult = "Get asset " + Node.strName + " data failed.";
            Log.Error("Update runtime Asset " + Node.strName + " error. " + response.ErrorMessage );
        }
        this.Node.Sts_LastActionDatetime = DateTime.Now;
    }

    [ExportMethod]
    public void AddEvent(int eventTypeID = -1, string additionalDescription = "Event created by FactoryTalk Optix")
    {
        CmmsApiService apiSingletonService = GatewayUtils.GetFiixCMMS_SingletonAPIService();
        ApiResponseModel response = apiSingletonService.AddAssetEvent(this.Node.id, eventTypeID, additionalDescription).Result;
        if (response.Success)
        {
            string eventName = "";
            IUANode eventTypeFolder = Project.Current.Find("AssetEventTypes");
            if (eventTypeFolder != null)
            {
                List<IUANode> eventTypes = eventTypeFolder.Children.Cast<IUANode>().ToList();
                IUANode selectedEvent = eventTypes.Find(ev => ev.GetVariable("id").Value == eventTypeID);
                if (selectedEvent != null) { eventName = selectedEvent.GetVariable("strEventName").Value; }
            }
            this.Node.Sts_LastActionResult = "Add Event " + eventName + " on Asset " + Node.strName + " succeeded.";
            Log.Info("Fiix Gateway", "Add Event " + eventName + " on Asset " + Node.strName + " succeeded.");
        }
        else
        {
            this.Node.Sts_LastActionResult = "Add Event with TypeID " + eventTypeID + " on Asset " + Node.strName + " failed. " ;
            Log.Error("Fiix Gateway","Add Event with TypeID " + eventTypeID + " on Asset " + Node.strName + " failed. " + response.ErrorMessage);
        }
        this.Node.Sts_LastActionDatetime = DateTime.Now;
    }

    [ExportMethod]
    public void AddMeterReading(string analogVariableName)
    {
        AnalogItem[] variableList = Node.GetNodesByType<AnalogItem>().ToArray();
        this.Node.Sts_LastActionResult = "Added MeterReading on " + Node.BrowseName;
        bool found = false;
        CmmsApiService apiSingletonService = GatewayUtils.GetFiixCMMS_SingletonAPIService();
        foreach (AnalogItem item in variableList) 
        {
            if (item.BrowseName == analogVariableName || analogVariableName.Trim() == "")
            {
                found = true;
                
                ApiResponseModel response = apiSingletonService.AddMeterReading(Node.id, item.EngineeringUnits.UnitId, item.Value).Result;
                if (response.Success)
                {
                    this.Node.Sts_LastActionResult += " with " + item.BrowseName + " of value " + item.Value + ";";
                }
                else
                {
                    this.Node.Sts_LastActionResult = " with " + item.BrowseName + " failed; "; 
                    Log.Error("Fiix Gateway","Add MeterReading " + item.BrowseName + " of value " + item.Value + " on " + Node.BrowseName + " failed. " + response.ErrorMessage);
                }
                if (analogVariableName.Trim() != "") break;
            }
        }
        if (!found) 
        {
            this.Node.Sts_LastActionResult += " failed, provided Variable Name is invalid.";
        }
        this.Node.Sts_LastActionDatetime = DateTime.Now;
        Log.Info("Fiix Gateway", this.Node.Sts_LastActionResult);
    }

    [ExportMethod]
    public void AddOfflineTracker(int reasonOfflineID = -1, int workOrderID=-1, string additionalInfo = "")
    {
        // Asset Online status is updated before add Offline tracker
        this.SwitchOffline();
        CmmsApiService apiSingletonService = GatewayUtils.GetFiixCMMS_SingletonAPIService();
        ApiResponseModel response = apiSingletonService.AddAssetOfflineTracker(this.Node.id, reasonOfflineID, workOrderID, additionalInfo).Result;
        if (response.Success)
        {
            if (reasonOfflineID != -1) this.Node.Sts_LastActionResult += "; Added Offline Tracker with ReasonID " + reasonOfflineID;
            else this.Node.Sts_LastActionResult += "; Added Offline Tracker with no ReasonID.";
            Log.Info("Fiix Gateway", this.Node.Sts_LastActionResult);
        }
        else
        {
            this.Node.Sts_LastActionResult = "Add Offline Tracker with reasonID " + reasonOfflineID + " on Asset " + Node.strName + " failed. ";
            Log.Error("Add Offline Tracker with reasonID " + reasonOfflineID + " on Asset " + Node.strName + " failed. " + response.ErrorMessage);
        }
        this.Node.Sts_LastActionDatetime = DateTime.Now;
        // Update asset' online status
        if (runtimeLogic != null)
        {
            DelayedTask updateStatusTask = new DelayedTask(UpdateRuntimeAsset, 200, (IUANode)runtimeLogic);
            updateStatusTask.Start();
        }
    }

    [ExportMethod]
    public void CloseOfflineTracker(int reasonOnlineID = -1, string additionalInfo = "", double hoursAffected = -1)
    {
        this.SwitchOnline();
        CmmsApiService apiSingletonService = GatewayUtils.GetFiixCMMS_SingletonAPIService();
        ApiResponseModel response = apiSingletonService.CloseLastAssetOfflineTracker(this.Node.id, reasonOnlineID, additionalInfo, hoursAffected).Result;

        this.Node.Sts_LastActionResult += response.Success? " Close OfflineTracker succeed." : " Close OfflineTracker with error.";
        Log.Info("Closing Offline Tracker on Asset " + Node.strName + " with result: " + response.ErrorMessage);
        this.Node.Sts_LastActionDatetime = DateTime.Now;
        // Update Online status of the asset
        if (runtimeLogic != null)
        {
            DelayedTask updateStatusTask = new DelayedTask(UpdateRuntimeAsset, 200, (IUANode)runtimeLogic);
            updateStatusTask.Start();
        }
    }

    [ExportMethod]
    public Fiix_AssetEvent[] GetAssetEvents(DateTime startDT, DateTime endDT)
    {
        //if (startDT == null) startDT = DateTime.Now.AddHours(-24);
        //if (endDT == null) endDT = DateTime.Now;

        CmmsApiService apiSingletonService = GatewayUtils.GetFiixCMMS_SingletonAPIService();
        ApiResponseModel response = apiSingletonService.FindAssetEventByAssetAndTimeRange(this.Node.id, (DateTime)startDT, (DateTime)endDT).Result;
        this.Node.Sts_LastActionDatetime = DateTime.Now;
        if (response.Success)
        {
            this.Node.Sts_LastActionResult = "Get asset historical events with " + response.objects.Length + " records.";
            return response.objects.OfType<Fiix_AssetEvent>().ToArray();
        }
        else
        {
            this.Node.Sts_LastActionResult = "Get asset historical events with no result";
            return null;
        }
    }

    [ExportMethod]
    public Fiix_MeterReading[] GetMeterReadings(DateTime startDT, DateTime endDT)
    {
        //if (startDT == null) startDT = DateTime.Now.AddHours(-24);
        //if (endDT == null) endDT = DateTime.Now;

        CmmsApiService apiSingletonService = GatewayUtils.GetFiixCMMS_SingletonAPIService();
        ApiResponseModel response = apiSingletonService.FindMeterReadingByAssetAndTimeRange(this.Node.id, (DateTime)startDT, (DateTime)endDT).Result;
        this.Node.Sts_LastActionDatetime = DateTime.Now;
        if (response.Success)
        {
            this.Node.Sts_LastActionResult = "Get asset historical meter readings with " + response.objects.Length + " records.";
            return response.objects.OfType<Fiix_MeterReading>().ToArray();
        }
        else
        {
            this.Node.Sts_LastActionResult = "Get asset historical meter readings with no result";
            return null;
        }
    }

    // Added Sep 2024 for v1.1 Work Order 
    // Add a WorkOrder to the current Site, then add a WorkOrderAsset to link the owner asset to the workorder
    [ExportMethod]
    public void AddWorkOrder(int WorkOrderStatusID = -1, int PriorityID = -1, int MaintenanceTypeID = -1, string Description = "", DateTime SuggestedStartDate = default(DateTime), DateTime SuggestedCompleteDate = default(DateTime))
    {
        // Get SiteID and AssetID
        int siteID = this.Node.intSiteID;
        int assetID = this.Node.id;
        DateTimeOffset startDate = SuggestedStartDate == default(DateTime)||SuggestedStartDate < new DateTime(2010,1,1) ? default(DateTimeOffset): new DateTimeOffset(SuggestedStartDate);
        DateTimeOffset completeDate = SuggestedCompleteDate == default(DateTime) || SuggestedCompleteDate < new DateTime(2010, 1, 1) ? default(DateTimeOffset) : new DateTimeOffset(SuggestedCompleteDate);

        // Add WorkOrder and WorkOrderAsset object using Fiix API
        if (WorkOrderStatusID == -1) 
        {
            this.Node.Sts_LastActionResult = "Invalid WorkOrder Status.";
            return;
        }
        CmmsApiService apiSingletonService = GatewayUtils.GetFiixCMMS_SingletonAPIService();
        ApiResponseModel response = apiSingletonService.AddWorkOrder(siteID, WorkOrderStatusID, PriorityID, MaintenanceTypeID, Description, startDate, completeDate).Result;
        if (response.Success)
        {
            int workOrderID = ((Fiix_WorkOrder)response.Object).id;
            string workOrderCode = ((Fiix_WorkOrder)response.Object).strCode;
            string statusName = "";
            IUANode statusFolder = Project.Current.Find("WorkOrderStatus");
            if (statusFolder != null && workOrderID != 0)
            {
                List<IUANode> statusNameList = statusFolder.Children.Cast<IUANode>().ToList();
                IUANode selectedStatus = statusNameList.Find(ev => ev.GetVariable("id").Value == WorkOrderStatusID);
                if (selectedStatus != null) { statusName = selectedStatus.BrowseName; }
            }

            ApiResponseModel response2 = apiSingletonService.AddWorkOrderAsset(workOrderID, assetID).Result;

            if (response2.Success) 
            {
                this.Node.Sts_LastActionResult = "Add WorkOrder with status " + statusName + " on Asset " + Node.strName + " succeeded, WorkOrder Code is " + workOrderCode + "." ;
                Log.Info("Fiix Gateway", "Add WorkOrder with status" + statusName + " on Asset " + Node.strName + " succeeded, WorkOrder Code is " + workOrderCode + ".");
            }
            else
            {
                this.Node.Sts_LastActionResult = "Add WorkOrderAsset with statusID " + WorkOrderStatusID + " on Asset " + Node.strName + " failed. ";
                Log.Error("Fiix Gateway", "Add WorkOrderAsset with statusID " + WorkOrderStatusID + " on Asset " + Node.strName + " failed. " + response2.ErrorMessage);
            }
        }
        else
        {
            this.Node.Sts_LastActionResult = "Add WorkOrder with statusID " + WorkOrderStatusID + " on Asset " + Node.strName + " failed. ";
            Log.Error("Fiix Gateway", "Add WorkOrder with statusID " + WorkOrderStatusID + " on Asset " + Node.strName + " failed. " + response.ErrorMessage);
        }
        this.Node.Sts_LastActionDatetime = DateTime.Now;
    }

    private NetLogicObject runtimeLogic = (NetLogicObject)Project.Current.Find("FiixGatewayRuntimeLogic");

    #region Auto-generated code, do not edit!
    protected new Asset Node => (Asset)base.Node;
#endregion
}
