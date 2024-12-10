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
using HttpAPIGateway;
using System.Diagnostics.Metrics;
using System.Data.SqlTypes;
using Newtonsoft.Json.Linq;
using System.Xml.Linq;
using static System.Formats.Asn1.AsnWriter;
using System.Linq;
using FTOptix.WebUI;
using FTOptix.EventLogger;
using Gpe.Integration.Fiix.Connector;
using Gpe.Integration.Fiix.Connector.Models;
using Gpe.Integration.Fiix.Connector.Models.Utilities;
using Gpe.Integration.Fiix.Connector.Services;
#endregion
/*
Fiix Gateway runtime UI script in MeterReading History faceplate to link and generate trending data to UI components.
=============================================================

Disclaimer of Warranty
THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT ARE PROVIDED "AS IS" WITHOUT WARRANTIES OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION, ALL IMPLIED WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE, NON-INFRINGEMENT OR OTHER VIOLATION OF RIGHTS. ROCKWELL AUTOMATION DOES NOT WARRANT OR MAKE ANY REPRESENTATIONS REGARDING THE USE, VALIDITY, ACCURACY, OR RELIABILITY OF, OR THE RESULTS OF ANY USE OF, OR OTHERWISE RESPECTING, THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT OR ANY WEB SITE LINKED TO THIS DOCUMENT 

Limitation of Liability
UNDER NO CIRCUMSTANCE (INCLUDING NEGLIGENCE AND TO THE FULLEST EXTEND PERMITTED BY APPLICABLE LAW) WILL ROCKWELL AUTOMATION BE LIABLE FOR ANY DIRECT, INDIRECT, SPECIAL, INCIDENTAL, PUNITIVE OR CONSEQUENTIAL DAMAGES (INCLUDING WITHOUT LIMITATION, BUSINESS INTERRUPTION, DELAYS, LOSS OF DATA OR PROFIT) ARISING OUT OF THE USE OR THE INABILITY TO USE THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT EVEN IF ROCKWELL AUTOMATION HAS BEEN ADVISED OF THE POSSIBILITY OF SUCH DAMAGES. IF USE OF SUCH MATERIALS RESULTS IN THE NEED FOR SERVICING, REPAIR OR CORRECTION OF USER EQUIPMENT OR DATA, USER ASSUMES ANY COSTS ASSOCIATED THEREWITH.

Copyright © Rockwell Automation, Inc.  All Rights Reserved. 

=============================================================
*/
public class MeterReadingTrendPanelRuntimeLogic : BaseNetLogic
{
    NetLogicObject runtimeLogic = (NetLogicObject)Project.Current.Find("FiixGatewayRuntimeLogic");
    string tempTrendDBName = "TempTrendDatabase";
    public override void Start()
    {
        // Insert code to be executed when the user-defined logic is started
        ((FTOptix.UI.DateTimePicker)this.Owner.Get("grp_MeterReadingTrend/grp_Trend/grp_TrendConfig/startDT")).Value = DateTime.Now.AddDays(-1);
        ((FTOptix.UI.DateTimePicker)this.Owner.Get("grp_MeterReadingTrend/grp_Trend/grp_TrendConfig/endDT")).Value = DateTime.Now;
        DisplayMeterReadingTrend();
    }

    public override void Stop()
    {
        // Clear Asset message when leaving the page.
        Asset asset = (Asset)InformationModel.Get(this.Owner.GetVariable("Asset").Value);
        asset.Sts_LastActionResult = "";
    }

    // Prepare Trend and Grid data at the same time; Display only one based on Switch selection
    [ExportMethod]
    public void DisplayMeterReadingTrend()
    {
        Asset asset = (Asset)InformationModel.Get(this.Owner.GetVariable("Asset").Value); //grp_MeterReadingTrend/grp_Trend/grp_TrendConfig/startDT
        DateTime startDT = ((FTOptix.UI.DateTimePicker)this.Owner.Get("grp_MeterReadingTrend/grp_Trend/grp_TrendConfig/startDT")).Value;
        DateTime endDT = ((FTOptix.UI.DateTimePicker)this.Owner.Get("grp_MeterReadingTrend/grp_Trend/grp_TrendConfig/endDT")).Value;

        // Convert from localtime to UTC
        DateTime startDTUTC = startDT.ToUniversalTime(); 
        DateTime endDTUTC = endDT.ToUniversalTime();

        Fiix_MeterReading[] data = GetMeterReadings(asset, startDTUTC, endDTUTC);
        IUANode dataModel = InformationModel.MakeObject("dataModel");
        if (data == null || data.Length == 0) return;

        IUAVariable MeterReadingUnits = (IUAVariable)Project.Current.Find("MeterReadingUnits");

        SQLiteStore trendDB = (SQLiteStore)runtimeLogic.Get(tempTrendDBName);
        if (trendDB == null)
        {
            trendDB = InformationModel.MakeObject<SQLiteStore>(tempTrendDBName);
            trendDB.Filename = "TempMeterReadingTrendDBFile";
            runtimeLogic.Add(trendDB);
        }

        string tempTableName = "Table_" + asset.id;

        // Clear trend table for the asset if exist
        Table table1 = trendDB.Tables.FirstOrDefault(t => t.BrowseName == tempTableName);
        if (table1!=null) table1.Delete();

        trendDB.AddTable(tempTableName);
        // SQLiteStoreTable thisTable = (SQLiteStoreTable)trendDB.Tables[0];
        SQLiteStoreTable thisTable = (SQLiteStoreTable)trendDB.Tables.FirstOrDefault(t => t.BrowseName == tempTableName);
        thisTable.AddColumn("Timestamp", OpcUa.DataTypes.DateTime);
        thisTable.AddColumn("LocalTimestamp", OpcUa.DataTypes.UtcTime);

        FTOptix.UI.Trend dataTrend = (FTOptix.UI.Trend)this.Owner.Get("grp_MeterReadingTrend/grp_Trend/Trend1"); //grp_MeterReadingTrend/grp_Trend/Trend1
        dataTrend.Pens.Clear();
        dataTrend.ClearTimeRanges();
        dataTrend.ClearTimeTraces();
        dataTrend.XAxis.Time = endDT;
        dataTrend.XAxis.Window = (uint)(endDT - startDT).TotalMilliseconds;
        dataTrend.XAxis.Follow = false;
        dataTrend.XAxis.SnapPosition = SnapPosition.Right;
        dataTrend.XAxis.Interactive = true;
        dataTrend.Model = trendDB.NodeId;
        dataTrend.Query = "SELECT * FROM " + tempTableName;
        var penColors = new Color[] { Colors.DarkSeaGreen, Colors.RosyBrown, Colors.PowderBlue, Colors.Thistle, Colors.DarkOliveGreen, Colors.LightSalmon, Colors.Khaki, Colors.DarkSlateBlue};
        int penIndex = 0;

        // Get MeterReading Units as DataTable Columns and Pens
        foreach (Fiix_MeterReading meterReading in data)
        {
            string newUnitName = "";
            // Get Engineering Unit name and code
            foreach (EngineeringUnitDictionaryItem unitItem in (UAManagedCore.Struct[])MeterReadingUnits.Value)
            {
                if (unitItem.UnitId == meterReading.intMeterReadingUnitsID)
                {
                    newUnitName = unitItem.DisplayName.Text;
                    bool colExist = false;
                    foreach (StoreColumn col in thisTable.Columns)
                    {
                        if (col.BrowseName == newUnitName)
                        {
                            colExist = true;
                            break;
                        }
                    }
                    if (!colExist)
                    {
                        thisTable.AddColumn(newUnitName, OpcUa.DataTypes.Float);
                        TrendPen myPen = InformationModel.Make<TrendPen>(newUnitName);
                        myPen.DataType = OpcUa.DataTypes.Float;
                        myPen.Color = penColors[penIndex];
                        dataTrend.Pens.Add(myPen);
                        penIndex++;
                    }
                    break;
                }
            }
        }

        // Insert MeterReading Values into DataTable
        var columnValues = new object[1,3];
        foreach (Fiix_MeterReading meterReading in data)
        {
            string newUnitName = "";
            // Get EventType name and code
            foreach (EngineeringUnitDictionaryItem unitItem in (UAManagedCore.Struct[])MeterReadingUnits.Value)
            {
                if (unitItem.UnitId == meterReading.intMeterReadingUnitsID)
                {
                    newUnitName = unitItem.DisplayName.Text;
                    //foreach (StoreColumn col in thisTable.Columns)
                    //{
                    //    if (col.BrowseName == newUnitName)
                    //    { break;}
                    //}
                    string[] columns = { "Timestamp", "LocalTimestamp", newUnitName};
                    DateTime s1 = DateTimeOffset.FromUnixTimeMilliseconds(meterReading.dtmDateSubmitted).ToLocalTime().DateTime;
                    s1 = DateTime.SpecifyKind(s1, DateTimeKind.Local);
                    DateTime s2 = DateTimeOffset.FromUnixTimeMilliseconds(meterReading.dtmDateSubmitted).UtcDateTime;
                    columnValues[0,0] = DateTimeOffset.FromUnixTimeMilliseconds(meterReading.dtmDateSubmitted).ToLocalTime().DateTime;
                    columnValues[0,1] = DateTimeOffset.FromUnixTimeMilliseconds(meterReading.dtmDateSubmitted).UtcDateTime;
                    columnValues[0,2] = meterReading.dblMeterReading;

                    thisTable.Insert(columns, columnValues);
                    //  dataModel for Grid
                    IUANode newReading = InformationModel.MakeObject<MeterReading>(meterReading.id.ToString());
                    newReading.GetVariable("strUnitName").Value = newUnitName;
                    newReading.GetVariable("dblMeterReading").Value = meterReading.dblMeterReading;
                    newReading.GetVariable("dtmDateSubmitted").Value = DateTimeOffset.FromUnixTimeMilliseconds(meterReading.dtmDateSubmitted).ToLocalTime().DateTime;
                    dataModel.Add(newReading);
                    // Sort by datetime
                    var readingList = dataModel.Children.Cast<IUANode>().ToList();
                    var cpCount = dataModel.Children.Count();
                    for (int i = cpCount - 1; i >= 0; i--)
                    {
                        try
                        {
                            if (DateTime.Compare(readingList[i].GetVariable("dtmDateSubmitted").Value, newReading.GetVariable("dtmDateSubmitted").Value) > 0) newReading.MoveUp();
                        }
                        catch { Log.Info("error when sort MeterReading Panel Grid data"); }
                    }
                    break;                         
                }
            }     
        }
        // Display in Trend
        dataTrend.Refresh();

        // Display in DataGrid
        FTOptix.UI.DataGrid dataGrid = (FTOptix.UI.DataGrid)this.Owner.Get("grp_MeterReadingTrend/grp_Trend/DataGrid1"); 
        if (dataGrid != null) dataGrid.Model = dataModel.NodeId;
        dataGrid.Columns[0].Width = 210;
    }

    private Fiix_MeterReading[] GetMeterReadings(Asset Node, DateTime startDT, DateTime endDT)
    {
        //if (startDT == null) startDT = DateTime.Now.AddHours(-24);
        //if (endDT == null) endDT = DateTime.Now;

        CmmsApiService apiSingletonService = GatewayUtils.GetFiixCMMS_SingletonAPIService();
        ApiResponseModel response = apiSingletonService.FindMeterReadingByAssetAndTimeRange(Node.id, (DateTime)startDT, (DateTime)endDT).Result;
        Node.Sts_LastActionDatetime = DateTime.Now;
        if (response.Success)
        {
            Node.Sts_LastActionResult = "Get asset meter readings from " + startDT.ToString("s") + " to " + endDT.ToString("s") + " with " + response.objects.Length + " records.";
            return response.objects.OfType<Fiix_MeterReading>().ToArray();
        }
        else
        {
            Node.Sts_LastActionResult = "Get asset meter readings from " + startDT.ToString("s") + " to " + endDT.ToString("s") + " with no result";
            return null;
        }
    }
}
