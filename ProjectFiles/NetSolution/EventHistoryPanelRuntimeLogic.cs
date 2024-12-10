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
using FTOptix.WebUI;
using FTOptix.EventLogger;
using Gpe.Integration.Fiix.Connector.Models;
using System.Linq;
using Gpe.Integration.Fiix.Connector.Services;
using Gpe.Integration.Fiix.Connector.Models.Utilities;
#endregion
/*
Fiix Gateway runtime UI script in Event History faceplate to link and fetch historical event data to UI components.
=============================================================

Disclaimer of Warranty
THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT ARE PROVIDED "AS IS" WITHOUT WARRANTIES OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION, ALL IMPLIED WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE, NON-INFRINGEMENT OR OTHER VIOLATION OF RIGHTS. ROCKWELL AUTOMATION DOES NOT WARRANT OR MAKE ANY REPRESENTATIONS REGARDING THE USE, VALIDITY, ACCURACY, OR RELIABILITY OF, OR THE RESULTS OF ANY USE OF, OR OTHERWISE RESPECTING, THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT OR ANY WEB SITE LINKED TO THIS DOCUMENT 

Limitation of Liability
UNDER NO CIRCUMSTANCE (INCLUDING NEGLIGENCE AND TO THE FULLEST EXTEND PERMITTED BY APPLICABLE LAW) WILL ROCKWELL AUTOMATION BE LIABLE FOR ANY DIRECT, INDIRECT, SPECIAL, INCIDENTAL, PUNITIVE OR CONSEQUENTIAL DAMAGES (INCLUDING WITHOUT LIMITATION, BUSINESS INTERRUPTION, DELAYS, LOSS OF DATA OR PROFIT) ARISING OUT OF THE USE OR THE INABILITY TO USE THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT EVEN IF ROCKWELL AUTOMATION HAS BEEN ADVISED OF THE POSSIBILITY OF SUCH DAMAGES. IF USE OF SUCH MATERIALS RESULTS IN THE NEED FOR SERVICING, REPAIR OR CORRECTION OF USER EQUIPMENT OR DATA, USER ASSUMES ANY COSTS ASSOCIATED THEREWITH.

Copyright © Rockwell Automation, Inc.  All Rights Reserved. 

=============================================================
*/
public class EventHistoryPanelRuntimeLogic : BaseNetLogic
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
            dataModel.Add(newEvent);
        }
        dataGrid.Model = dataModel.NodeId;
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
}
