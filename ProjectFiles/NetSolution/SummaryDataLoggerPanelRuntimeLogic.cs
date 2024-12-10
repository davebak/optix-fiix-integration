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
using System.Linq;
using System.Collections.Generic;
using FTOptix.EventLogger;
#endregion
/*
Fiix Gateway Summary UI script to display Push Agent datalogger status for meter reading data sending.
=============================================================

Disclaimer of Warranty
THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT ARE PROVIDED "AS IS" WITHOUT WARRANTIES OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION, ALL IMPLIED WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE, NON-INFRINGEMENT OR OTHER VIOLATION OF RIGHTS. ROCKWELL AUTOMATION DOES NOT WARRANT OR MAKE ANY REPRESENTATIONS REGARDING THE USE, VALIDITY, ACCURACY, OR RELIABILITY OF, OR THE RESULTS OF ANY USE OF, OR OTHERWISE RESPECTING, THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT OR ANY WEB SITE LINKED TO THIS DOCUMENT 

Limitation of Liability
UNDER NO CIRCUMSTANCE (INCLUDING NEGLIGENCE AND TO THE FULLEST EXTEND PERMITTED BY APPLICABLE LAW) WILL ROCKWELL AUTOMATION BE LIABLE FOR ANY DIRECT, INDIRECT, SPECIAL, INCIDENTAL, PUNITIVE OR CONSEQUENTIAL DAMAGES (INCLUDING WITHOUT LIMITATION, BUSINESS INTERRUPTION, DELAYS, LOSS OF DATA OR PROFIT) ARISING OUT OF THE USE OR THE INABILITY TO USE THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT EVEN IF ROCKWELL AUTOMATION HAS BEEN ADVISED OF THE POSSIBILITY OF SUCH DAMAGES. IF USE OF SUCH MATERIALS RESULTS IN THE NEED FOR SERVICING, REPAIR OR CORRECTION OF USER EQUIPMENT OR DATA, USER ASSUMES ANY COSTS ASSOCIATED THEREWITH.

Copyright © Rockwell Automation, Inc.  All Rights Reserved. 

=============================================================
*/
public class SummaryDataLoggerPanelRuntimeLogic : BaseNetLogic
{
    FTOptix.UI.DataGrid dataGridLoggerStore;
    FTOptix.UI.DataGrid dataGridVariablesToLog;
    FTOptix.Store.Store edgeDataStore;
    FTOptix.DataLogger.DataLogger edgeDataLogger;
    FTOptix.UI.Label textStoreCounts, textBufferRowCount, textLastSendDT, textLastSendResult;
    FTOptix.UI.TextBox tagFilterTextBox, textDeadbandValue;
    FTOptix.UI.Button buttonRefresh, buttonClearBuffer, buttonClearDataStore;
    FTOptix.UI.ComboBox comboBoxSampleMode;
    FTOptix.UI.DurationPicker durationSampling, durationPolling;
    IUAObject runtimeLogic;
    string tsName = "LocalTimestamp";
    string storeTableName = "MeterReadingDataLogger";

    public override void Start()
    {
    // Auto Refresh is enabled in the DataGrid by default
        //var autoRefreshCheckBox = LogicObject.Owner.Find<CheckBox>("ValueAutoRefresh");
        //var activeVariable = autoRefreshCheckBox.CheckedVariable;
        //activeVariable.VariableChange += OnActiveVariableChanged;

        dataGridLoggerStore = (DataGrid)this.Owner.Find("DataGridLoggerStore");
        dataGridVariablesToLog = (DataGrid)this.Owner.Find("DataGridVariablesToLog");
        edgeDataStore = (Store)Project.Current.Find("MeterReadingDataStore");
        edgeDataLogger = (DataLogger)Project.Current.Find("MeterReadingDataLogger");

        textStoreCounts = (Label)this.Owner.Find("ValueStoreCounts");
        textBufferRowCount = (Label)this.Owner.Find("ValueBufferRowCount");
        textLastSendDT = (Label)this.Owner.Find("ValueLastSendDT");
        textLastSendResult = (Label)this.Owner.Find("ValueLastSendResult");
        buttonRefresh = (Button)this.Owner.Find("ButtonRefresh");
        buttonClearBuffer = (Button)this.Owner.Find("ButtonClearBuffer");
        buttonClearDataStore = (Button)this.Owner.Find("ButtonClearDataStore");
        tagFilterTextBox = (TextBox)this.Owner.Find("TagFilterString");
        runtimeLogic = (IUAObject)Project.Current.Find("FiixGatewayRuntimeLogic");
        textDeadbandValue = (TextBox)LogicObject.Owner.Find("ValueDeadBand");
        comboBoxSampleMode = (ComboBox)this.Owner.Find("ValueSamplingMode");
        durationSampling = (DurationPicker)this.Owner.Find("ValueSamplingPeriod");
        durationPolling = (DurationPicker)this.Owner.Find("ValuePollingTime");

        // Wire up buttons click events and datalogger configuration 
        var sendDT = runtimeLogic.GetVariable("Sts_PushAgentLastSendDatetime");
        var sendResult = runtimeLogic.GetVariable("Sts_PushAgentLastSendResult");
        buttonRefresh.OnMouseClick += RefreshDataEventHandler;
        buttonClearBuffer.OnMouseClick += ClearBufferEventHandler;
        buttonClearDataStore.OnMouseClick += ClearDataStoreEventHandler;
        textLastSendResult.TextVariable.SetDynamicLink(sendResult);
        textLastSendDT.TextVariable.SetDynamicLink(sendDT);

        // Link DataLogger Configuration to Model
        comboBoxSampleMode.SelectedValueVariable.SetDynamicLink(edgeDataLogger.GetVariable("SamplingMode"), DynamicLinkMode.ReadWrite);
        durationSampling.ValueVariable.SetDynamicLink(edgeDataLogger.GetVariable("SamplingPeriod"), DynamicLinkMode.ReadWrite);
        durationPolling.ValueVariable.SetDynamicLink(edgeDataLogger.GetVariable("PollingPeriod"), DynamicLinkMode.ReadWrite);
        textDeadbandValue.TextVariable.SetDynamicLink(edgeDataLogger.GetVariable("DefaultDeadBandValue"), DynamicLinkMode.ReadWrite);

        void RefreshDataEventHandler(object sender, MouseClickEvent e)
        {
            RefreshData();
        }

        void ClearBufferEventHandler(object sender, MouseClickEvent e)
        {
            runtimeLogic.ExecuteMethod("ClearPushAgentTempStore");
            LoadStatusPanelData();
        }

        void ClearDataStoreEventHandler(object sender, MouseClickEvent e)
        {
            runtimeLogic.ExecuteMethod("ClearDataLoggerStore");
            LoadStatusPanelData();
        }

        RefreshData();
    }

    public override void Stop()
    {
        refreshTask?.Dispose();
    }

    [ExportMethod]
    public void RefreshData()
    {
        LoadDataGridVariablesToLog();
        LoadDataGridLoggerStore();
        LoadStatusPanelData();
    }

    private void LoadDataGridVariablesToLog()
    {
        // Link DataGrid to Model data
        dataGridVariablesToLog.Model = edgeDataLogger.GetObject("VariablesToLog").NodeId;
        dataGridVariablesToLog.Refresh();
    }

    private void LoadDataGridLoggerStore()
    {
        // Link DataGrid to Model data
        dataGridLoggerStore.Model = edgeDataStore.NodeId;
        List<string> variablesList = edgeDataLogger.VariablesToLog.Select(x => x.BrowseName).ToList();


        // Form columns name array from filter string
        string nameFilter = tagFilterTextBox.Text;
        if (nameFilter != null && nameFilter.Trim() != "")
        {
            string[] filterColumnArray = nameFilter.Split(",",StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            variablesList.RemoveAll(x => !filterColumnArray.Any(x.Contains));
            var columnList = variablesList.Select(x => "\""+x.Trim()+"\"").ToList();

            // add name filter into Query
            string columnsString = string.Join(",", columnList);
            dataGridLoggerStore.Query = "SELECT " + tsName + "," + columnsString + " FROM \"" + storeTableName + "\" ORDER BY " + tsName + " DESC";
        }
        else   // No filter given
        {
            dataGridLoggerStore.Query = "SELECT * FROM \"" + storeTableName + "\" ORDER BY " + tsName + "  DESC";
        }

        // Prepare Columns
        variablesList.Insert(0, tsName);
        string[] columns = variablesList.ToArray();
        dataGridLoggerStore.Columns.Clear();
        for (int k = 0; k < columns.Length; k++)
        {
            DataGridColumn column1 = InformationModel.Make<DataGridColumn>(columns[k]);
            column1.OrderBy = "{Item}/" + columns[k];
            if (k == 0) column1.Title = tsName;
            else
            {
                // column1.Title = columns[k];
                // Title specific for Fiix using MeterUnit
                int assetIDPos = columns[k].IndexOf("_AssetID");
                int EUPos = columns[k].IndexOf("_EU");
                int EUIDPos = columns[k].IndexOf("_EUID");
                if (assetIDPos < 0 || EUPos < 0 || EUIDPos < 0) column1.Title = "_";
                else column1.Title = columns[k].Substring(0,assetIDPos) + "_" + columns[k].Substring(EUPos+3,EUIDPos-EUPos-3);
            }
            DataGridLabelItemTemplate itemTemplate = InformationModel.MakeObject<DataGridLabelItemTemplate>("DataItemTemplate");
            IUAVariable tempVariable = null;
            itemTemplate.TextVariable.SetDynamicLink(tempVariable, DynamicLinkMode.Read);
            itemTemplate.TextVariable.GetVariable("DynamicLink").Value = "{Item}/" + columns[k];
            column1.DataItemTemplate = itemTemplate;

            dataGridLoggerStore.Columns.Add(column1);
        }
        dataGridLoggerStore.Refresh();
    }

    private void LoadStatusPanelData()
    {
        // Get DataStore row count
        object[,] resultSet;
        string[] header;

        edgeDataStore.Query("SELECT COUNT(*) FROM " + storeTableName, out header, out resultSet);
        if (resultSet.Rank != 2) goto BufferCount;
        var rowCount = (Int64)resultSet[0, 0];

        // Get DataStore column count
        var columnCount = edgeDataStore.Tables[0].Columns.Count - 2;
        textStoreCounts.Text = rowCount + " / " + columnCount;

        // Get PushAgentStore (buffer) row count
        BufferCount:
        string pushAgentStoreBrowseName = "PushAgentStore";
        SQLiteStore tempStore = (SQLiteStore)runtimeLogic.Find(pushAgentStoreBrowseName);
        if (tempStore == null) return;
        string tableName = "";
        // Fiix gateway always use RowPerVariable
        if (!runtimeLogic.GetVariable("Cfg_PushFullSample").Value || true)
        {
            tableName = "PushAgentTableRowPerVariable";
        }
        else
        {
            tableName = "PushAgentTableDataLogger";
        }
        tempStore.Query("SELECT COUNT(*) FROM " + tableName, out header, out resultSet);
        if (resultSet.Rank != 2)
        {
            Log.Info("PushAgentStore", "Read from buffer store for row count return Rank !=2.");
            return;
        }
        textBufferRowCount.Text = (Int64)resultSet[0, 0] + " records";
    }



    private void OnActiveVariableChanged(object sender, VariableChangeEventArgs e)
    {
        if ((bool)e.NewValue)
        {
            refreshTask = new PeriodicTask(RefreshDataGrid, 1100, LogicObject);
            refreshTask.Start();
        }
        else
        {
            refreshTask?.Dispose();
        }
    }

    public void RefreshDataGrid()
    {
        dataGridLoggerStore.Refresh();
    }

    private PeriodicTask refreshTask;
}
