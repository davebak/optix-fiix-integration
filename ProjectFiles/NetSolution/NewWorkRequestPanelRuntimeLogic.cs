#region Using directives
using System;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.NativeUI;
using FTOptix.HMIProject;
using FTOptix.CoreBase;
using FTOptix.NetLogic;
using FTOptix.DataLogger;
using FTOptix.SQLiteStore;
using FTOptix.Store;
using FTOptix.EventLogger;
using FTOptix.Core;
using System.Collections.Generic;
using System.Linq;
using Gpe.Integration.Fiix.Connector.Models;
using System.ComponentModel.Design;
using Gpe.Integration.Fiix.Connector.Models.Utilities;
using Gpe.Integration.Fiix.Connector.Services;
using HttpAPIGateway;
using System.Xml.Linq;
#endregion
/*
Fiix Gateway runtime UI script in New Work Request Widget to facilitate new Work Order creation.
=============================================================

Disclaimer of Warranty
THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT ARE PROVIDED "AS IS" WITHOUT WARRANTIES OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION, ALL IMPLIED WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE, NON-INFRINGEMENT OR OTHER VIOLATION OF RIGHTS. ROCKWELL AUTOMATION DOES NOT WARRANT OR MAKE ANY REPRESENTATIONS REGARDING THE USE, VALIDITY, ACCURACY, OR RELIABILITY OF, OR THE RESULTS OF ANY USE OF, OR OTHERWISE RESPECTING, THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT OR ANY WEB SITE LINKED TO THIS DOCUMENT 

Limitation of Liability
UNDER NO CIRCUMSTANCE (INCLUDING NEGLIGENCE AND TO THE FULLEST EXTEND PERMITTED BY APPLICABLE LAW) WILL ROCKWELL AUTOMATION BE LIABLE FOR ANY DIRECT, INDIRECT, SPECIAL, INCIDENTAL, PUNITIVE OR CONSEQUENTIAL DAMAGES (INCLUDING WITHOUT LIMITATION, BUSINESS INTERRUPTION, DELAYS, LOSS OF DATA OR PROFIT) ARISING OUT OF THE USE OR THE INABILITY TO USE THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT EVEN IF ROCKWELL AUTOMATION HAS BEEN ADVISED OF THE POSSIBILITY OF SUCH DAMAGES. IF USE OF SUCH MATERIALS RESULTS IN THE NEED FOR SERVICING, REPAIR OR CORRECTION OF USER EQUIPMENT OR DATA, USER ASSUMES ANY COSTS ASSOCIATED THEREWITH.

Copyright © Rockwell Automation, Inc.  All Rights Reserved. 

=============================================================
*/
public class NewWorkRequestPanelRuntimeLogic : BaseNetLogic
{
    public override void Start()
    {
        // Populate Sites
        availableListBox = (ListBox)this.Owner.Find("ListBoxAvailableAssets");
        selectedListBox = (ListBox)this.Owner.Find("ListBoxSelectedAssets");
        filterAsset = (TextBox)this.Owner.Find("TextBoxAssetFilter");
        textDescription = (TextBox)this.Owner.Find("TextBoxDescription");
        dtCompletion = (DateTimePicker)this.Owner.Find("DTCompletion");
        labelResult = (Label)this.Owner.Find("LabelResult");
        labelResult.Text = "";

        assetFolder = Project.Current.Find<Folder>("Assets");
        if (assetFolder == null)
        {
            Log.Error("Fiix Gateway", "Cannot find assets folder in New Work Request panel logic.");
            return;
        }
        List<IUANode> assetNodeList = assetFolder.Children.Cast<IUANode>().ToList();
        tempSiteFolder = InformationModel.MakeObject<Folder>("tempSiteFolder");
        this.Owner.Add(tempSiteFolder);
        foreach (var assetNode in assetNodeList)
        {
            NodePointer newPointer = (NodePointer)InformationModel.MakeNodePointer(assetNode.BrowseName);
            newPointer.Value = assetNode.NodeId;
            tempSiteFolder.Add(newPointer);
        }
        comboBoxSite = (ComboBox)this.Owner.Find("ComboBoxSite");
        comboBoxSite.Model = tempSiteFolder.NodeId;

        // Populate default Completion Date, Maintenance Type and Priority
        ((FTOptix.UI.DateTimePicker)this.Owner.Find("DTCompletion")).Value = DateTime.Now.AddDays(1);

        maintenanceTypeFolder = Project.Current.Find<Folder>("MaintenanceTypes");
        if (maintenanceTypeFolder == null)
        {
            Log.Error("Fiix Gateway", "Cannot find Maintenance Type folder in New Work Request panel logic.");
            return;
        }
        comboBoxType = (ComboBox)this.Owner.Find("ComboBoxType");
        comboBoxType.Model = maintenanceTypeFolder.NodeId;

        priorityFolder = Project.Current.Find<Folder>("Priorities");
        if (priorityFolder == null)
        {
            Log.Error("Fiix Gateway", "Cannot find priority folder in New Work Request panel logic.");
            return;
        }
        comboBoxPriority = (ComboBox)this.Owner.Find("ComboBoxPriority");
        comboBoxPriority.Model = priorityFolder.NodeId;

        // Set objects
        tempAssetFolder = InformationModel.Make<Folder>("tempAssetFolder");
        this.Owner.Add(tempAssetFolder);
        tempSelectedFolder = InformationModel.Make<Folder>("tempSelectedFolder");
        this.Owner.Add(tempSelectedFolder);
        tempAvailableFolder = InformationModel.Make<Folder>("tempAvailableFolder");
        this.Owner.Add(tempAvailableFolder);

        SiteChangeAction();
    }

    public override void Stop()
    {
        // Insert code to be executed when the user-defined logic is stopped
    }

    [ExportMethod]
    public void SiteChangeAction()
    {
        // Re-populate temporary local asset folder
        foreach (IUANode node in tempAssetFolder.Children) node.Delete();
        tempAssetFolder.Children.Clear();

        IUANode selectedSite = InformationModel.Get(((NodePointer)InformationModel.Get(comboBoxSite.SelectedItem)).Value);
        if (selectedSite != null) AddChildAssetsToTempFolder(selectedSite);

        FilterChangeAction();
    }

    [ExportMethod]
    public void FilterChangeAction()
    {
        foreach (IUANode node in tempAvailableFolder.Children) node.Delete();
        tempAvailableFolder.Children.Clear();

        List<IUANode> shortList = tempAssetFolder.Children.Cast<IUANode>().ToList();
        if (filterAsset.Text != "") shortList = shortList.FindAll(x => x.BrowseName.Contains(filterAsset.Text));
        foreach (IUANode child in shortList)
        {
            NodePointer newPointer = (NodePointer)InformationModel.MakeNodePointer(child.BrowseName);
            newPointer.Value = child.NodeId;
            tempAvailableFolder.Add(newPointer);
        }
        availableListBox.Model = tempAvailableFolder.NodeId;
    }

    [ExportMethod]
    public void AddSelectedAsset()
    {
        labelResult.Text = "";

        var selectedA = availableListBox.SelectedItem;
        if (selectedA == null) { labelResult.Text = "Please select an available asset."; return; }
        IUANode selectedAsset = InformationModel.Get(((NodePointer)InformationModel.Get(selectedA)).Value);
        if (tempSelectedFolder.Children.Cast<NodePointer>().ToList().Exists(a => a.BrowseName == selectedAsset.BrowseName || (NodeId)a.Value == selectedAsset.NodeId ))
        {
            labelResult.Text = "The asset has already been selected.";
            return;
        }
        NodePointer newPointer = (NodePointer)InformationModel.MakeNodePointer(selectedAsset.BrowseName);
        newPointer.Value = selectedAsset.NodeId;
        tempSelectedFolder.Add(newPointer);
        selectedListBox.Model = tempSelectedFolder.NodeId;
        selectedListBox.Refresh();
    }

    [ExportMethod]      
    public void RemoveSelectedAsset() 
    {
        labelResult.Text = "";
        var selectedA = selectedListBox.SelectedItem;
        if (selectedA == null) { labelResult.Text = "Please select an asset to remove from selection."; return; }
        IUANode selectedAsset = InformationModel.Get(((NodePointer)InformationModel.Get(selectedA)).Value);
        tempSelectedFolder.Children.Remove(selectedAsset);
        selectedAsset.Delete();
        selectedListBox.Model = tempSelectedFolder.NodeId;
        selectedListBox.Refresh();
    }

    [ExportMethod]
    public void SubmitRequest() 
    {
        labelResult.Text = "";
        if (textDescription.Text.Trim() == "") { labelResult.Text = "Please enter Description for the request."; return; }
        if (tempSelectedFolder.Children.Count < 1) { labelResult.Text = "Please select Asset(s) for the request."; return; }

        // Get Open status
        IUANode statusFolder = Project.Current.Find<Folder>("WorkOrderStatus");
        List<IUANode> statusList = statusFolder.Children.Cast<IUANode>().ToList();
        IUANode openStatus = statusList.Find(x => x.BrowseName.ToUpper().Contains("OPEN"));
        if (openStatus == null)
        {
            Log.Error("Fiix Gateway", "New Work Request with Open status object not found.");
            labelResult.Text = "Get Open WorkOrder status error.";
            return;
        }

        CmmsApiService apiSingletonService = GatewayUtils.GetFiixCMMS_SingletonAPIService();
        int siteID = InformationModel.Get(((NodePointer)InformationModel.Get(comboBoxSite.SelectedItem)).Value).GetVariable("id").Value;
        int WorkOrderStatusID = openStatus.GetVariable("id").Value;
        int PriorityID = InformationModel.Get(comboBoxPriority.SelectedItem).GetVariable("id").Value;
        int MaintenanceTypeID = InformationModel.Get(comboBoxType.SelectedItem).GetVariable("id").Value;
        string Description = textDescription.Text; 
        DateTimeOffset startDate = DateTime.Now;
        DateTimeOffset completeDate = dtCompletion.Value.ToUniversalTime();

        ApiResponseModel response = apiSingletonService.AddWorkOrder(siteID, WorkOrderStatusID, PriorityID, MaintenanceTypeID, Description, startDate, completeDate).Result;
        if (response.Success)
        {
            int workOrderID = ((Fiix_WorkOrder)response.Object).id;
            string workOrderCode = ((Fiix_WorkOrder)response.Object).strCode;
            string assetList = "";

            foreach (NodePointer assetPointer in tempSelectedFolder.Children)
            {
                try
                {
                    int assetID = InformationModel.Get(((NodePointer)InformationModel.Get(assetPointer.Value)).Value).GetVariable("id").Value;
                    ApiResponseModel response2 = apiSingletonService.AddWorkOrderAsset(workOrderID, assetID).Result;

                    if (response2.Success)
                    {
                        assetList = assetList + assetPointer.BrowseName + ", ";
                    }
                    else
                    {
                        Log.Error("Fiix Gateway New Work Request", "Add WorkOrderAsset with workOrderID " + workOrderID + " on Asset " + assetPointer.BrowseName + " failed. " + response2.ErrorMessage);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Fiix Gateway New Work Request", "Add WorkOrderAsset with workOrderID " + workOrderID + " on Asset " + assetPointer.BrowseName + " failed. " + ex.Message);
                }
            }
            labelResult.Text = "Work Request " + workOrderCode + " is created for asset: " + assetList;
        }
        else
        {
            labelResult.Text = "Add WorkOrder with description " + Description + " failed. ";
            Log.Error("Fiix Gateway New Work Request", "Add WorkOrder with description " + Description + " failed. " + response.ErrorMessage);
        }


    }

    [ExportMethod]
    public void ClearSelection()
    {
        foreach (IUANode node in tempSelectedFolder.Children) node.Delete();
        tempSelectedFolder.Children.Clear();
        selectedListBox.Refresh();
    }

    private void AddChildAssetsToTempFolder(IUANode parentNode)
    {
        if (parentNode == null) return;
        // Get existing object nodes children
        var existingChildren = parentNode.Children.Cast<IUANode>().ToList();
        existingChildren.RemoveAll(x => x.NodeClass != NodeClass.Object || x.BrowseName.Contains("DesignTimeLogic"));
        foreach (var child in existingChildren)
        {
            NodePointer newPointer = (NodePointer)InformationModel.MakeNodePointer(child.BrowseName);
            newPointer.Value = child.NodeId;
            tempAssetFolder.Add(newPointer);
            AddChildAssetsToTempFolder(child);
        }
    }

    Folder priorityFolder;
    Folder maintenanceTypeFolder;
    Folder assetFolder;
    Folder tempSiteFolder;
    Folder tempAssetFolder;
    Folder tempAvailableFolder;
    Folder tempSelectedFolder;
    ComboBox comboBoxSite;
    ComboBox comboBoxPriority;
    ComboBox comboBoxType;
    ListBox availableListBox;
    ListBox selectedListBox;
    TextBox filterAsset;
    TextBox textDescription;
    Label labelResult;
    DateTimePicker dtCompletion;
}
