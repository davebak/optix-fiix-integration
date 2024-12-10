#region Using directives
using System;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.DataLogger;
using FTOptix.NativeUI;
using FTOptix.UI;
using FTOptix.CoreBase;
using FTOptix.SQLiteStore;
using FTOptix.Store;
using FTOptix.OPCUAServer;
using FTOptix.RAEtherNetIP;
using FTOptix.CommunicationDriver;
using FTOptix.Core;
using FTOptix.WebUI;
using System.Collections.Generic;
using System.Linq;
using FTOptix.EventLogger;
#endregion
/*
Fiix Gateway Summary UI script to display local Fiix assets model data and configuration.
=============================================================

Disclaimer of Warranty
THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT ARE PROVIDED "AS IS" WITHOUT WARRANTIES OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION, ALL IMPLIED WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE, NON-INFRINGEMENT OR OTHER VIOLATION OF RIGHTS. ROCKWELL AUTOMATION DOES NOT WARRANT OR MAKE ANY REPRESENTATIONS REGARDING THE USE, VALIDITY, ACCURACY, OR RELIABILITY OF, OR THE RESULTS OF ANY USE OF, OR OTHERWISE RESPECTING, THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT OR ANY WEB SITE LINKED TO THIS DOCUMENT 

Limitation of Liability
UNDER NO CIRCUMSTANCE (INCLUDING NEGLIGENCE AND TO THE FULLEST EXTEND PERMITTED BY APPLICABLE LAW) WILL ROCKWELL AUTOMATION BE LIABLE FOR ANY DIRECT, INDIRECT, SPECIAL, INCIDENTAL, PUNITIVE OR CONSEQUENTIAL DAMAGES (INCLUDING WITHOUT LIMITATION, BUSINESS INTERRUPTION, DELAYS, LOSS OF DATA OR PROFIT) ARISING OUT OF THE USE OR THE INABILITY TO USE THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT EVEN IF ROCKWELL AUTOMATION HAS BEEN ADVISED OF THE POSSIBILITY OF SUCH DAMAGES. IF USE OF SUCH MATERIALS RESULTS IN THE NEED FOR SERVICING, REPAIR OR CORRECTION OF USER EQUIPMENT OR DATA, USER ASSUMES ANY COSTS ASSOCIATED THEREWITH.

Copyright © Rockwell Automation, Inc.  All Rights Reserved. 

=============================================================
*/
public class SummaryConfigurationPanelRuntimeLogic : BaseNetLogic
{
    DataGrid dataGrid;
    Label labelSiteCount;
    Label labelAssetCount;
    Label labelMeterReadingCount,labelARPCount;
    Switch switchSending ;
    IUANode runtimeLogic;

    public override void Start()
    {
        // Generate support data for Asset Tree DataGrid, organize Assets under Assets folder into a temperary SQLLiteStore
        dataGrid = (DataGrid)this.Owner.Find("DataGridAssetTree");
        runtimeLogic = Project.Current.Find("FiixGatewayRuntimeLogic");
        labelSiteCount = (Label)this.Owner.Find("LabelSiteCount");
        labelAssetCount = (Label)this.Owner.Find("LabelAssetCount");
        labelMeterReadingCount = (Label)this.Owner.Find("LabelMeterReadingCount");
        labelARPCount = (Label)this.Owner.Find("LabelARPCount");
        switchSending = (Switch)this.Owner.Find("SwitchSending");
        switchSending.CheckedVariable.SetDynamicLink(runtimeLogic.GetVariable("Set_MeterReadingStoreAndSend"), DynamicLinkMode.ReadWrite);
        LoadDataToDataGridAssetTree();
    }

    public override void Stop()
    {
        // Do not clear tempDB during runtime
        //IUANode tempDB = (SQLiteStore)runtimeLogic.Find("tempDB");
        //if (tempDB != null) tempDB.Delete();
    }

    private void LoadDataToDataGridAssetTree()
    {
        SQLiteStore tempDB;
        if (runtimeLogic == null)
        {
            Log.Error("Update Assets in Runtime error: Couldnot find DesignTimeLogic to get configuration");
            return;
        }
        IUANode modelFolder = runtimeLogic.Owner.Owner.Find("Assets");
        IUANode AssetType = runtimeLogic.Owner.Find("Asset");
        tempDB = (SQLiteStore)runtimeLogic.Find("tempDB");
        string ColPrefix = "Level_";
        int siteCount = 0, assetCount = 0, meterReadingCount = 0, ARPCount = 0;

        siteCount = modelFolder.Children.Cast<IUANode>().ToList().Count;

        if (tempDB != null)
        {
            //foreach (Table tbl in tempDB.Tables) tempDB.RemoveTable(tbl.BrowseName);
            //tempDB.Tables.Clear();
            //runtimeLogic.Remove(tempDB);
            //tempDB.Delete();
            goto DisplayOnly;
        }
        tempDB = InformationModel.MakeObject<SQLiteStore>("tempDB");
        //tempDB.Filename = "AssetTreeTempDB";
        runtimeLogic.Add(tempDB);
        tempDB.InMemory = true;
        tempDB.AddTable("AssetTree");
        var meterCountVAR = InformationModel.MakeVariable("MeterReadingCount", OpcUa.DataTypes.Int16);
        var ARPCountVAR = InformationModel.MakeVariable("ARPCount", OpcUa.DataTypes.Int16);
        tempDB.Add(meterCountVAR);
        tempDB.Add(ARPCountVAR);    

        // Convert Assets into DataGrid table values

        // Calculate the size of the array
        var arraySize = CalculateArraySize(modelFolder);

        // Convert nested objects to a two-dimensional array, first column is Assets folder node to be ignored
        var values = new string?[arraySize.numRows, arraySize.numColumns];

        int indexRow = 0;

        ConvertToTwoDimensionalArray(modelFolder, values, ref indexRow, 0);

        (int numRows, int numColumns) CalculateArraySize(IUANode obj)
        {
            int numRows = CalculateNumRows(obj);
            int numColumns = CalculateNestedLevels(obj);
            return (numRows, numColumns);
        }

        int CalculateNumRows(IUANode obj)
        {
            int numRows = 1;
            var existingChildren = obj.Children.Cast<IUANode>().ToList();
            existingChildren.RemoveAll(x => x.NodeClass != NodeClass.Object || (x is ARPData) || x.BrowseName.Contains("DesignTimeLogic"));
            foreach (var child in existingChildren)
            {
                numRows += CalculateNumRows(child);
            }
            return numRows;
        }

        int CalculateNestedLevels(IUANode obj)
        {
            var existingChildren = obj.Children.Cast<IUANode>().ToList();
            existingChildren.RemoveAll(x => x.NodeClass != NodeClass.Object || (x is ARPData) || x.BrowseName.Contains("DesignTimeLogic"));

            if (existingChildren.Count == 0)
            {
                return 1; // This is the deepest level
            }

            int maxChildLevels = 0;
            foreach (var child in existingChildren)
            {
                maxChildLevels = Math.Max(maxChildLevels, CalculateNestedLevels(child));
            }

            return maxChildLevels + 1;
        }

        void ConvertToTwoDimensionalArray(IUANode obj, string?[,] result, ref int rowIndex, int columnIndex)
        {
            if (obj != null)
            {
                var existingChildren = obj.Children.Cast<IUANode>().ToList();
                existingChildren.RemoveAll(x => x.NodeClass != NodeClass.Object || (x is ARPData) || x.BrowseName.Contains("DesignTimeLogic"));

                // Check configured AnalogVariable Count
                var variables = obj.Children.Cast<IUANode>().ToList();
                int countAV = 0;
                variables.RemoveAll(x => x.NodeClass != NodeClass.Variable);

                foreach (IUANode v in variables)
                {
                    if (v is AnalogItem)
                    {
                        countAV++;
                        meterReadingCount++;
                    }
                }

                // Check configured ARP Count
                var ARPs = obj.Children.Cast<IUANode>().ToList();
                int countARP = 0;
                ARPs.RemoveAll(x => x.NodeClass != NodeClass.Object || !x.GetType().FullName.Contains("ARPData"));
                ARPs.RemoveAll(x => x.NodeClass != NodeClass.Object || !(x is ARPData));

                countARP = ARPs.Count;
                ARPCount += countARP;

                result[rowIndex, columnIndex] = obj.BrowseName + (countAV == 0 && countARP == 0 ? "" : " (" + (countAV == 0 ? "" : "MeterReadings:" + countAV ) + (countARP == 0? "" : ", ARP:" + countARP )) + (countAV == 0 && countARP == 0 ? "" : ")");

                //result[rowIndex, columnIndex] = obj.BrowseName;
                rowIndex++;

                int nextColumnIndex = columnIndex + 1;

                foreach (var child in existingChildren)
                {
                    ConvertToTwoDimensionalArray(child, result, ref rowIndex, nextColumnIndex);
                    //nextColumnIndex++; // Increment for each child
                }
            }
            else
            {
                result[rowIndex, columnIndex] = "";
            }
        }

        // Prepare columns names array for table insert
        string[] columns = new string[arraySize.numColumns - 1];

        for (int i = 0; i < arraySize.numColumns - 1; i++)
        {
            if (i == 0)
            {
                tempDB.AddColumn("AssetTree", "Site", OpcUa.DataTypes.String);
                columns[i] = "Site";
            }
            else
            {
                tempDB.AddColumn("AssetTree", ColPrefix + i, OpcUa.DataTypes.String);
                columns[i] = ColPrefix + i;
            }
        }
        // First Column/Row is Assets Folder, being removed by coping to new array.
        var nodeArray = new string?[arraySize.numRows - 1, arraySize.numColumns - 1];

        for (int i = 1; i < arraySize.numRows; i++)
        {
            for (int j = 1; j < arraySize.numColumns; j++)
            {
                nodeArray[i - 1, j - 1] = values[i, j];
            }
        }

        tempDB.GetVariable("MeterReadingCount").Value = meterReadingCount;
        tempDB.GetVariable("ARPCount").Value = ARPCount;
        try
        {
            tempDB.Insert("AssetTree", columns, nodeArray);
        }
        catch (Exception e)
        {
            Log.Error("Prepare DataGrid for AssetTree", $"Failed to insert data into temp store: {e.Message}.");
        }

        DisplayOnly:
        dataGrid.Model = tempDB.NodeId;
        dataGrid.Query = "SELECT * FROM AssetTree ORDER BY Site DESC"; // + ColPrefix + "1";
        for (int k = 0; k < tempDB.Tables[0].Columns.Count; k++)
        {
            DataGridColumn column1 = InformationModel.Make<DataGridColumn>(ColPrefix + k);
            //column1.OrderBy = "{Item}/" + columns[k];
            if (k == 0) column1.Title = "Site";
            else column1.Title = tempDB.Tables[0].Columns[k].BrowseName;
            DataGridLabelItemTemplate itemTemplate = InformationModel.MakeObject<DataGridLabelItemTemplate>("DataItemTemplate");
            IUAVariable tempVariable = null;
            itemTemplate.TextVariable.SetDynamicLink(tempVariable, DynamicLinkMode.Read);
            itemTemplate.TextVariable.GetVariable("DynamicLink").Value = "{Item}/" + tempDB.Tables[0].Columns[k].BrowseName;
            column1.DataItemTemplate = itemTemplate;

            dataGrid.Columns.Add(column1);
        }
        dataGrid.Refresh();

        //Get tempDB row count:
        object[,] resultSet;
        string[] header;
        int rowCount = 0;
        tempDB.Query("SELECT COUNT(*) FROM AssetTree", out header, out resultSet);
        if (resultSet.Rank == 2) rowCount = Convert.ToInt32(resultSet[0, 0]);

        assetCount = rowCount - siteCount;
        labelSiteCount.Text = siteCount.ToString();
        labelAssetCount.Text = assetCount.ToString();
        labelMeterReadingCount.Text = tempDB.GetVariable("MeterReadingCount").Value;
        labelARPCount.Text = tempDB.GetVariable("ARPCount").Value;
    }
}
