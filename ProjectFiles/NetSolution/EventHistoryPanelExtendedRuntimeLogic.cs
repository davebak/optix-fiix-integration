#region Using directives
using System;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.HMIProject;
using FTOptix.UI;
using FTOptix.DataLogger;
using FTOptix.NativeUI;
using FTOptix.NetLogic;
using FTOptix.CoreBase;
using FTOptix.SQLiteStore;
using FTOptix.Store;
using FTOptix.EventLogger;
using FTOptix.Core;
using HttpAPIGateway;
using System.Collections.Generic;
using System.Linq;
using Gpe.Integration.Fiix.Connector;
using System.Threading.Tasks.Dataflow;
using Gpe.Integration.Fiix.Connector.Models;
using Gpe.Integration.Fiix.Connector.Services;
using Gpe.Integration.Fiix.Connector.Models.Utilities;
#endregion

public class EventHistoryPanelExtendedRuntimeLogic : BaseNetLogic
{
    public override void Start()
    {
        // Insert code to be executed when the user-defined logic is started
        ((FTOptix.UI.DateTimePicker)this.Owner.Get("grp_EventHistory/grp_EventHistory/grp_Filter/startDT")).Value = DateTime.Now.AddDays(-1);
        ((FTOptix.UI.DateTimePicker)this.Owner.Get("grp_EventHistory/grp_EventHistory/grp_Filter/endDT")).Value = DateTime.Now;
        DisplayEventHistory();
    }

    public override void Stop()
    {
        // Clear Asset message when leaving the page.
        Asset asset = (Asset)InformationModel.Get(this.Owner.GetVariable("Asset").Value);
        asset.Sts_LastActionResult = "";
    }

    [ExportMethod]
    public void DisplayEventHistory()
    {
        Asset asset = (Asset)InformationModel.Get(this.Owner.GetVariable("Asset").Value);
        DateTime startDT = ((FTOptix.UI.DateTimePicker)this.Owner.Get("grp_EventHistory/grp_EventHistory/grp_Filter/startDT")).Value;
        DateTime endDT = ((FTOptix.UI.DateTimePicker)this.Owner.Get("grp_EventHistory/grp_EventHistory/grp_Filter/endDT")).Value;
        // Convert from localtime to UTC
        DateTime startDTUTC = startDT.ToUniversalTime();
        DateTime endDTUTC = endDT.ToUniversalTime();

        FTOptix.UI.DataGrid dataGrid = (FTOptix.UI.DataGrid)this.Owner.Get("grp_EventHistory/grp_EventHistory/ScrollView1/DataGrid1");
        IUANode dataModel = InformationModel.MakeObject("dataModel");
        Fiix_AssetEvent[] data = GetAssetEvents(asset, startDTUTC, endDTUTC);
        IUANode AssetEventTypes = Project.Current.Find("AssetEventTypes");
        Fiix_User[] users = GatewayUtils.GetFiixCMMS_SingletonAPIService().FindUsers().Result.objects.OfType<Fiix_User>().ToArray();
        //List<string> workOrderIDs = new List<string>();

        if (data == null || data.Length == 0) return;

        foreach (Fiix_AssetEvent assetEvent in data)
        {
            string newEventName = "";
            string newEventCode = "";
            string newEventDescription = "";

            // Get EventType name and code
            foreach (IUANode eventType in AssetEventTypes.Children)
            {
                if (eventType.GetVariable("id").Value == assetEvent.intAssetEventTypeID)
                {
                    newEventName = eventType.GetVariable("strEventName").Value;
                    newEventCode = eventType.GetVariable("strEventCode").Value;
                    newEventDescription = eventType.GetVariable("strEventDescription").Value;
                    break;
                }
            }
            IUANode newEvent = InformationModel.MakeObject<AssetEvent>(assetEvent.id.ToString());
            newEvent.GetVariable("strEventName").Value = newEventName;
            newEvent.GetVariable("strEventCode").Value = newEventCode;
            newEvent.GetVariable("strEventDescription").Value = newEventDescription ?? "";
            newEvent.GetVariable("strAdditionalDescription").Value = assetEvent.strAdditionalDescription ?? "";
            newEvent.GetVariable("dtmDateSubmitted").Value = DateTimeOffset.FromUnixTimeMilliseconds(assetEvent.dtmDateSubmitted).ToLocalTime().DateTime;
            if (users != null)
            {
                Fiix_User fiix_User = Array.Find<Fiix_User>(users, user => user.id == assetEvent.intSubmittedByUserID);
                if (fiix_User != null) newEvent.GetVariable("strSubmittedByUser").Value = fiix_User.strFullName;
            }

            //newEvent.GetVariable("intWorkOrderID").Value = assetEvent.intWorkOrderID;
            //if (assetEvent.intWorkOrderID != 0) workOrderIDs.Add(assetEvent.intWorkOrderID.ToString());
            dataModel.Add(newEvent);
        }
        dataGrid.Model = dataModel.NodeId;

        // Group events and prepare model for charts
        if (dataModel.Children.Count > 0)
        {
            // New Chart model type
            IUAObjectType ChartModelType = InformationModel.MakeObjectType("ChartModelType");
            IUAVariable GroupName = InformationModel.MakeVariable("GroupName", OpcUa.DataTypes.String);
            IUAVariable GroupValue = InformationModel.MakeVariable("GroupValue", OpcUa.DataTypes.Int32);
            ChartModelType.Add(GroupName);
            ChartModelType.Add(GroupValue);

            // Group by event type and user name
            chartModelFolder = InformationModel.MakeObject<Folder>("chartModelFolder");
            List<AssetEvent> newEvents = dataModel.Children.Cast<AssetEvent>().ToList() ;
            var resultGroup  = newEvents.GroupBy(e => e.strEventName).Select(c => new { groupName = c.Key, groupCount = c.Count() });
            foreach (var itemGroup in  resultGroup)
            {
                var newGroup = InformationModel.MakeObject(itemGroup.groupName, ChartModelType.NodeId);
                newGroup.GetVariable("GroupName").Value = itemGroup.groupName;
                newGroup.GetVariable("GroupValue").Value = itemGroup.groupCount;
                chartModelFolder.Add(newGroup);
            }
            // By User
            chartModelUserFolder = InformationModel.MakeObject<Folder>("chartModelUserFolder");
            var resultUserGroup = newEvents.GroupBy(e => e.strSubmittedByUser).Select(c => new { groupName = c.Key, groupCount = c.Count() });
            foreach (var itemGroup in resultUserGroup)
            {
                var newGroup = InformationModel.MakeObject(itemGroup.groupName, ChartModelType.NodeId);
                newGroup.GetVariable("GroupName").Value = itemGroup.groupName;
                newGroup.GetVariable("GroupValue").Value = itemGroup.groupCount;
                chartModelUserFolder.Add(newGroup);
            }

            // Format charts
            barChart = (HistogramChart)this.Owner.Find("BarChart");
            pieChart = (PieChart)this.Owner.Find("PieChart");
            IUANode switchGroup = this.Owner.Find("SwitchGroup");
            groupSelectionSwitch = (Switch)switchGroup.Find("Switch");
            IUAVariable tempVariable = null;

            var item = barChart.Find<Alias>("Item");
            if (item != null)  item.Kind = ChartModelType.NodeId;
            barChart.LabelVariable.SetDynamicLink(tempVariable,DynamicLinkMode.ReadWrite);
            barChart.LabelVariable.GetVariable("DynamicLink").Value = "{Item}/GroupName";
            barChart.ValueVariable.SetDynamicLink(tempVariable,DynamicLinkMode.ReadWrite);
            barChart.ValueVariable.GetVariable("DynamicLink").Value = "{Item}/GroupValue";
            if (groupSelectionSwitch.Checked) barChart.Model = chartModelUserFolder.NodeId;
            else  barChart.Model = chartModelFolder.NodeId;
            barChart.Refresh();

            item = pieChart.Find<Alias>("Item");
            if (item != null) item.Kind = ChartModelType.NodeId;
            pieChart.LabelVariable.SetDynamicLink(tempVariable, DynamicLinkMode.ReadWrite);
            pieChart.LabelVariable.GetVariable("DynamicLink").Value = "{Item}/GroupName";
            pieChart.ValueVariable.SetDynamicLink(tempVariable, DynamicLinkMode.ReadWrite);
            pieChart.ValueVariable.GetVariable("DynamicLink").Value = "{Item}/GroupValue";
            if (groupSelectionSwitch.Checked) pieChart.Model = chartModelUserFolder.NodeId;
            else pieChart.Model = chartModelFolder.NodeId;
            pieChart.Refresh();

            groupSelectionSwitch.OnUserValueChanged += GroupSelectionSwitch_OnUserValueChanged;
        }
                    

        // ****************
        // Get a list of all related work orders in one call, then allocate to each event. Not apply as WorkOrderID is actually not returned from API call, different from API Reference
        // ****************
        //if (workOrderIDs.Count > 0)
        //{
        //    Fiix_WorkOrder[] fiixWorkOrders = GetWorkOrders(workOrderIDs.Distinct().ToArray());
        //    if (fiixWorkOrders != null && fiixWorkOrders.Length > 0)
        //    {
        //        foreach (IUANode newEvent in dataModel.Children)
        //        {
        //            var relatedWO = Array.Find<Fiix_WorkOrder>(fiixWorkOrders, wo => wo.id == newEvent.GetVariable("intWorkOrderID").Value);
        //            if (relatedWO != null)
        //            {
        //                newEvent.GetVariable("strWorkOrder").Value = relatedWO.strDescription;
        //                newEvent.GetVariable("intWorkOrderPriorityID").Value = relatedWO.intPriorityID;
        //                newEvent.GetVariable("intWorkOrderStatusID").Value = relatedWO.intWorkOrderStatusID;
        //            }
        //        }
        //    }
        //}
    }

    private void GroupSelectionSwitch_OnUserValueChanged(object sender, UserValueChangedEvent e)
    {
        if (groupSelectionSwitch.Checked) barChart.Model = chartModelUserFolder.NodeId;
        else barChart.Model = chartModelFolder.NodeId;
        barChart.Refresh();

        if (groupSelectionSwitch.Checked) pieChart.Model = chartModelUserFolder.NodeId;
        else pieChart.Model = chartModelFolder.NodeId;
        pieChart.Refresh();

    }

    private Fiix_AssetEvent[] GetAssetEvents(Asset Node, DateTime startDT, DateTime endDT)
    {
        //if (startDT == null) startDT = DateTime.Now.AddHours(-24);
        //if (endDT == null) endDT = DateTime.Now;

        CmmsApiService apiSingletonService = GatewayUtils.GetFiixCMMS_SingletonAPIService();
        ApiResponseModel response = apiSingletonService.FindAssetEventByAssetAndTimeRange(Node.id, (DateTime)startDT, (DateTime)endDT).Result;
        Node.Sts_LastActionDatetime = DateTime.Now;
        if (response.Success)
        {
            Node.Sts_LastActionResult = "Get asset historical events with " + response.objects.Length + " records.";
            return response.objects.OfType<Fiix_AssetEvent>().ToArray();
        }
        else
        {
            Node.Sts_LastActionResult = "Get asset historical events with no result";
            return null;
        }
    }

    private Fiix_WorkOrder[] GetWorkOrders(string[] workOrderIDs)
    {
        CmmsApiService apiSingletonService = GatewayUtils.GetFiixCMMS_SingletonAPIService();
        ApiResponseModel response = apiSingletonService.FindWorkOrderByIDsAndTimeRange(workOrderIDs, null, null).Result;
        if (response.Success)
        {
            Log.Info("Fiix Gateway", "Get Work Orders from historical events with " + response.objects.Length + " records.");
            return response.objects.OfType<Fiix_WorkOrder>().ToArray();
        }
        else
        {
            Log.Info("Fiix Gateway", "Get Work Orders from historical events with no result");
            return null;
        }
    }

    private HistogramChart barChart; 
    private PieChart pieChart; 
    private Switch groupSelectionSwitch;
    private Folder chartModelFolder;
    private Folder chartModelUserFolder;

}
