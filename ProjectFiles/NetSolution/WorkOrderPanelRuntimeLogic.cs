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
using System.Collections.Generic;
using System.Linq;
using Gpe.Integration.Fiix.Connector.Models.Utilities;
using Gpe.Integration.Fiix.Connector.Models;
using Gpe.Integration.Fiix.Connector.Services;
using HttpAPIGateway;
#endregion
/*
Fiix Gateway runtime UI script in MeterReading faceplate to add new work order, link and generate historical data to UI components.
=============================================================

Disclaimer of Warranty
THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT ARE PROVIDED "AS IS" WITHOUT WARRANTIES OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION, ALL IMPLIED WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE, NON-INFRINGEMENT OR OTHER VIOLATION OF RIGHTS. ROCKWELL AUTOMATION DOES NOT WARRANT OR MAKE ANY REPRESENTATIONS REGARDING THE USE, VALIDITY, ACCURACY, OR RELIABILITY OF, OR THE RESULTS OF ANY USE OF, OR OTHERWISE RESPECTING, THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT OR ANY WEB SITE LINKED TO THIS DOCUMENT 

Limitation of Liability
UNDER NO CIRCUMSTANCE (INCLUDING NEGLIGENCE AND TO THE FULLEST EXTEND PERMITTED BY APPLICABLE LAW) WILL ROCKWELL AUTOMATION BE LIABLE FOR ANY DIRECT, INDIRECT, SPECIAL, INCIDENTAL, PUNITIVE OR CONSEQUENTIAL DAMAGES (INCLUDING WITHOUT LIMITATION, BUSINESS INTERRUPTION, DELAYS, LOSS OF DATA OR PROFIT) ARISING OUT OF THE USE OR THE INABILITY TO USE THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT EVEN IF ROCKWELL AUTOMATION HAS BEEN ADVISED OF THE POSSIBILITY OF SUCH DAMAGES. IF USE OF SUCH MATERIALS RESULTS IN THE NEED FOR SERVICING, REPAIR OR CORRECTION OF USER EQUIPMENT OR DATA, USER ASSUMES ANY COSTS ASSOCIATED THEREWITH.

Copyright © Rockwell Automation, Inc.  All Rights Reserved. 

=============================================================
*/
public class WorkOrderPanelRuntimeLogic : BaseNetLogic
{
    string NotSetName = "Not Set";
    public override void Start()
    {       
        startDT = DateTime.Now.AddDays(-7); ;
        endDT = DateTime.Now;

        GetStatus();
        GetPriority();
        GetMaintenanceType(); 
        GetWorkOrderToSQLite();
        PopulateAddPanelTask();
    }

    public override void Stop()
    {
        try
        {
            if (notSetPriority != null) { priorityFolder.Remove(notSetPriority); }
            if (notSetMaintenanceType != null) { maintenanceTypeFolder.Remove(notSetMaintenanceType);  }
            if (allStatus != null) { statusFolder.Remove(allStatus);  }
        }
        catch (Exception ex) { Log.Info("Fiix Gateway", "Closing work order panel, " + ex.Message); }
    }

    [ExportMethod]
    public void RefreshAddPanelWithDelay()
    {
        getAddTask = new DelayedTask(PopulateAddPanelTask, 500, LogicObject);
        getAddTask.Start();
    }

    [ExportMethod]
    public void RefreshHistoryPanelWithDelay()
    {
        getHistoryTask = new DelayedTask(PopulateHistoryPanelTask, 500, LogicObject);
        getHistoryTask.Start();
    }

    private void PopulateAddPanelTask()
    {
        ((FTOptix.UI.DateTimePicker)this.Owner.Find("DateAndTimeStart")).Value = DateTime.Now;
        ((FTOptix.UI.DateTimePicker)this.Owner.Find("DateAndTimeComplete")).Value = DateTime.Now.AddDays(1);
        // Set AssetID
        var assetNodeID = LogicObject.Owner.FindVariable("AddPanelAsset");
        Asset asset = (Asset)InformationModel.Get(this.Owner.GetVariable("Asset").Value);
        if (assetNodeID != null && asset != null) assetNodeID.Value = asset.NodeId;
        PopulatePriority();
        PopulateMaintenanceType();
        PopulateStatus();
    }

    private void PopulateHistoryPanelTask()
    {
        startDTPicker = (FTOptix.UI.DateTimePicker)this.Owner.Find("startDT");
        endDTPicker = (FTOptix.UI.DateTimePicker)this.Owner.Find("endDT");
        dataGrid = (FTOptix.UI.DataGrid)this.Owner.Find("DataGrid1");

        startDTPicker.Value = startDT;
        endDTPicker.Value = endDT;

        var refreshBit = this.Owner.FindVariable("RefreshBit");
        if (refreshBit!=null) refreshBit.VariableChange += RefreshBit_VariableChange;

        PopulateStatus();
        PopulateWorkOrderDataGrid();
    }

    private void RefreshBit_VariableChange(object sender, VariableChangeEventArgs e)
    {
        startDT = startDTPicker.Value;
        endDT = endDTPicker.Value;
        GetWorkOrderToSQLite();
        PopulateWorkOrderDataGrid();
    }

    private void GetPriority()
    {
        // Get priority list and add a "not set" item
        priorityFolder = Project.Current.Find<Folder>("Priorities");
        if (priorityFolder == null)
        {
            Log.Error("Fiix Gateway", "Cannot find Priorities folder in WorkOrder panel logic.");
            return;
        }
        List<IUANode> priorityNodeList = priorityFolder.Children.Cast<IUANode>().ToList();
        notSetPriority = (Priority)priorityNodeList.Find(x => x.BrowseName == NotSetName);
        if (notSetPriority == null || notSetPriority.NodeId == null)
        {
            notSetPriority = InformationModel.MakeObject<Priority>(NotSetName);
            notSetPriority.GetVariable("id").Value = -1;
            notSetPriority.GetVariable("strName").Value = NotSetName;
            priorityFolder.Add(notSetPriority);
        }
    }

    private void PopulatePriority()
    {
        priorityList = (ListBox)this.Owner.Find("ListBoxPriority");
        priorityList.Model = priorityFolder.NodeId;
        priorityList.SelectedItem = notSetPriority.NodeId;
    }

    private void GetMaintenanceType()
    {
        // Get maintenanceType list and add a "not set" item
        maintenanceTypeFolder = Project.Current.Find<Folder>("MaintenanceTypes");
        if (maintenanceTypeFolder == null)
        {
            Log.Error("Fiix Gateway", "Cannot find Maintenance Type folder in WorkOrder panel logic.");
            return;
        }
        List<IUANode> maintenanceTypeNodeList = maintenanceTypeFolder.Children.Cast<IUANode>().ToList();
        notSetMaintenanceType = (MaintenanceType)maintenanceTypeNodeList.Find(x => x.BrowseName == NotSetName);
        if (notSetMaintenanceType == null || notSetMaintenanceType.NodeId == null)
        {
            notSetMaintenanceType = InformationModel.MakeObject<MaintenanceType>(NotSetName);
            notSetMaintenanceType.GetVariable("id").Value = -1;
            notSetMaintenanceType.GetVariable("strName").Value = NotSetName;
            maintenanceTypeFolder.Add(notSetMaintenanceType);
        }
    }
    private void PopulateMaintenanceType()
    {
        typeList = (ListBox)this.Owner.Find("ListBoxType");
        typeList.Model = maintenanceTypeFolder.NodeId;
        typeList.SelectedItem = notSetMaintenanceType.NodeId;
    }

    private void GetStatus()
    {
        statusFolder = Project.Current.Find<Folder>("WorkOrderStatus");
        if (statusFolder == null )
        {
            Log.Error("Fiix Gateway", "Work Order panel initiation with object not found.");
            return;
        }
        List<IUANode> statusList = statusFolder.Children.Cast<IUANode>().ToList();

        // Get Open status and All status for default selection
        openStatus = statusList.Find(x => x.BrowseName.ToUpper().Contains("OPEN"));
        allStatus = (WOStatus)statusList.Find(x => x.BrowseName == "All");
        if (allStatus == null || allStatus.NodeId == null)
        {
            allStatus = InformationModel.MakeObject<WOStatus>("All");
            allStatus.GetVariable("id").Value = -1;
            allStatus.GetVariable("strName").Value = "All";
            statusFolder.Add(allStatus);
        }
    }

    private void PopulateStatus()
    {
        try
        {
            comboBoxStatus = (ComboBox)this.Owner.Find("ComboBoxStatus");
            if (comboBoxStatus != null)
            {
                comboBoxStatus.Model = statusFolder.NodeId;
                if (openStatus != null && openStatus.NodeId != null) comboBoxStatus.SelectedItem = openStatus.NodeId;
            }

            comboBoxHistoryStatus = (ComboBox)this.Owner.Find("ComboBoxHistoryStatus");
            if (comboBoxHistoryStatus != null)
            {
                comboBoxHistoryStatus.Model = statusFolder.NodeId;
                comboBoxHistoryStatus.SelectedItem = allStatus.NodeId;
            }
        }
        catch { }
    }

    public void PopulateWorkOrderDataGrid()
    {
        // ** Code for using object as Model, which doesn't support dynamic Status filtering, display work order list based on selected Status, Backup only **
        // Remove dataModel children;
        //if (dataModel != null)
        //{
        //    foreach (var node in dataModel.Children) 
        //    {
        //        dataModel.Remove(node);
        //        dataModel.Children.Clear();
        //    }
        //}

        //if (workOrderList != null && workOrderList.Count > 0)
        //{
        //    WOStatus selectedStatus = (WOStatus)InformationModel.Get(comboBoxStatus.SelectedItem);
        //    if (selectedStatus.BrowseName == "All")
        //    {
        //        foreach (WorkOrder wo in workOrderList) dataModel.Add(wo);
        //    }
        //    else
        //    {
        //        List<WorkOrder> filteredList = workOrderList.Where<WorkOrder>(x => x.strWorkOrderStatus == selectedStatus.strName).ToList();
        //        foreach (WorkOrder wo2 in filteredList) dataModel.Add(wo2);
        //    }
        //}

        selectedStatus = (WOStatus)InformationModel.Get(comboBoxHistoryStatus.SelectedItem);
        // Clean existing columns
        foreach (var item in dataGrid.Columns) item.Delete();
        dataGrid.Columns.Clear();

        dataGrid.Model = tempDB.NodeId;
        if (selectedStatus.strName == "All") dataGrid.Query = "SELECT DateCreated, Code, Status, Priority, Description, MaintType FROM GridData ORDER BY DateCreated DESC ";
        else dataGrid.Query = "SELECT DateCreated, Code, Priority, Description, MaintType FROM GridData WHERE Status = '" + selectedStatus.strName + "' ORDER BY DateCreated DESC ";

        // Prepare DataGrid columns
        for (int k = 0; k < columns.Length; k++)
        {
            if (selectedStatus.strName != "All" && columns[k] == "Status") continue;
            DataGridColumn column1 = InformationModel.Make<DataGridColumn>(columns[k]);
            column1.Title = columns[k];
            DataGridLabelItemTemplate itemTemplate = InformationModel.MakeObject<DataGridLabelItemTemplate>("DataItemTemplate");
            IUAVariable tempVariable = null;
            itemTemplate.TextVariable.SetDynamicLink(tempVariable, DynamicLinkMode.Read);
            itemTemplate.TextVariable.GetVariable("DynamicLink").Value = "{Item}/" + columns[k];
            column1.DataItemTemplate = itemTemplate;
            //if (columns[k] == "DateCreated") column1.Width = 180;
            if (columns[k] == "Priority") column1.Width = 88;
            if (columns[k] == "DateCreated") column1.Width = 165;
            dataGrid.Columns.Add(column1);
        }
        dataGrid.Refresh();
    }

    private void GetWorkOrderToSQLite()
    {
        // List<WorkOrder> woList = new List<WorkOrder>();
        tempDB = InformationModel.MakeObject<SQLiteStore>("tempDB");
        tempDB.InMemory = true;
        this.Owner.Add(tempDB);
        tempDB.AddTable("GridData");
        tempDB.AddColumn("GridData", "DateCreated", OpcUa.DataTypes.String);
        tempDB.AddColumn("GridData", "Code", OpcUa.DataTypes.String);
        tempDB.AddColumn("GridData", "Status", OpcUa.DataTypes.String);
        tempDB.AddColumn("GridData", "Priority", OpcUa.DataTypes.String);
        tempDB.AddColumn("GridData", "Description", OpcUa.DataTypes.String);
        tempDB.AddColumn("GridData", "MaintType", OpcUa.DataTypes.String);

        Asset asset = (Asset)InformationModel.Get(this.Owner.GetVariable("Asset").Value);

        // Convert from localtime to UTC
        DateTime startDTUTC = startDT.ToUniversalTime();
        DateTime endDTUTC = endDT.ToUniversalTime();

        CmmsApiService apiSingletonService = GatewayUtils.GetFiixCMMS_SingletonAPIService();
        ApiResponseModel response = apiSingletonService.FindWorkOrderByAssetIDAndTimeRange(asset.id.ToString(), (DateTime)startDTUTC, (DateTime)endDTUTC).Result;
        asset.Sts_LastActionDatetime = DateTime.Now;
        Fiix_WorkOrder[] data = null;
        if (response.Success)
        {
            asset.Sts_LastActionResult = "Get Work Orders from " + startDT.ToString("s") + " to " + endDT.ToString("s") + " with " + response.objects.Length + " records.";
            data = response.objects.OfType<Fiix_WorkOrder>().ToArray();
        }
        else
        {
            Log.Error("Fiix Gateway", "Get Work Order history for asset " + asset.BrowseName + " with error " + response.ErrorMessage);
        }
        if (data == null || data.Length == 0)
        {
            asset.Sts_LastActionResult = "Get Work Orders from " + startDT.ToString("s") + " to " + endDT.ToString("s") + " with no result";
            return;
        }

        IUANode priorityFolder = Project.Current.Find<Folder>("Priorities");
        IUANode woStatusFolder = Project.Current.Find<Folder>("WorkOrderStatus");
        IUANode maintTypeFolder = Project.Current.Find<Folder>("MaintenanceTypes");
        List<Priority> priorityList = priorityFolder.Children.Cast<Priority>().ToList();
        List<WOStatus> statusList = woStatusFolder.Children.Cast<WOStatus>().ToList();
        List<MaintenanceType> typeList = maintTypeFolder.Children.Cast<MaintenanceType>().ToList();

        var values = new object[data.Length, 6];
        int index = 0;

        foreach (Fiix_WorkOrder workOrder in data)
        {
            string newWorkOrderPriority = "";
            string newWorkOrderStatus = "";
            string newWorkOrderMaintType = "";

            // Get WorkOrder status, priority, and type name
            var priority = priorityList.Find(item => item.id == workOrder.intPriorityID);
            if (priority != null) newWorkOrderPriority = priority.strName;

            var status = statusList.Find(item => item.id == workOrder.intWorkOrderStatusID);
            if (status != null) newWorkOrderStatus = status.strName;

            var type = typeList.Find(item => item.id == workOrder.intMaintenanceTypeID);
            if (type != null) newWorkOrderMaintType = type.strName;

            // ** Code for using Object as model, which does not support dynamic change of status filtering 
            //WorkOrder newWorkOrder = InformationModel.MakeObject<WorkOrder>(workOrder.id.ToString());
            //newWorkOrder.GetVariable("strPriority").Value = newWorkOrderPriority;
            //newWorkOrder.GetVariable("strWorkOrderStatus").Value = newWorkOrderStatus;
            //newWorkOrder.GetVariable("strDescription").Value = workOrder.strDescription;
            //newWorkOrder.GetVariable("strCode").Value = workOrder.strCode;
            //newWorkOrder.GetVariable("strMaintenanceType").Value = newWorkOrderMaintType;
            //newWorkOrder.GetVariable("dtCreated").Value = DateTimeOffset.FromUnixTimeMilliseconds(workOrder.dtmDateCreated).ToLocalTime().DateTime;
            //woList.Add(newWorkOrder);
            values[index, 0] = (DateTimeOffset.FromUnixTimeMilliseconds(workOrder.dtmDateCreated).ToLocalTime().DateTime).ToString("g");
            values[index, 1] = workOrder.strCode;
            values[index, 2] = newWorkOrderStatus;
            values[index, 3] = newWorkOrderPriority;
            values[index, 4] = workOrder.strDescription;
            values[index, 5] = newWorkOrderMaintType;
            index++;
        }
        tempDB.Insert("GridData", columns, values);
    }

    WOStatus allStatus, selectedStatus;
    IUANode dataModel, openStatus;
    FTOptix.UI.DataGrid dataGrid;
    SQLiteStore tempDB;
    string[] columns = { "DateCreated", "Code", "Status", "Priority", "Description", "MaintType" };

    ComboBox comboBoxStatus;
    ComboBox comboBoxHistoryStatus;
    ListBox priorityList;
    ListBox typeList;
    Priority notSetPriority;
    MaintenanceType notSetMaintenanceType;
    Folder statusFolder;
    Folder priorityFolder;
    Folder maintenanceTypeFolder;
    private DelayedTask getHistoryTask, getAddTask;
    FTOptix.UI.DateTimePicker startDTPicker, endDTPicker;
    DateTime startDT, endDT;
}
