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
using System.Xml.Linq;
using FTOptix.WebUI;
using FTOptix.EventLogger;
#endregion
/*
Fiix Gateway runtime UI script in MeterReading entry faceplate to add new meter reading to asset.
=============================================================

Disclaimer of Warranty
THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT ARE PROVIDED "AS IS" WITHOUT WARRANTIES OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION, ALL IMPLIED WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE, NON-INFRINGEMENT OR OTHER VIOLATION OF RIGHTS. ROCKWELL AUTOMATION DOES NOT WARRANT OR MAKE ANY REPRESENTATIONS REGARDING THE USE, VALIDITY, ACCURACY, OR RELIABILITY OF, OR THE RESULTS OF ANY USE OF, OR OTHERWISE RESPECTING, THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT OR ANY WEB SITE LINKED TO THIS DOCUMENT 

Limitation of Liability
UNDER NO CIRCUMSTANCE (INCLUDING NEGLIGENCE AND TO THE FULLEST EXTEND PERMITTED BY APPLICABLE LAW) WILL ROCKWELL AUTOMATION BE LIABLE FOR ANY DIRECT, INDIRECT, SPECIAL, INCIDENTAL, PUNITIVE OR CONSEQUENTIAL DAMAGES (INCLUDING WITHOUT LIMITATION, BUSINESS INTERRUPTION, DELAYS, LOSS OF DATA OR PROFIT) ARISING OUT OF THE USE OR THE INABILITY TO USE THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT EVEN IF ROCKWELL AUTOMATION HAS BEEN ADVISED OF THE POSSIBILITY OF SUCH DAMAGES. IF USE OF SUCH MATERIALS RESULTS IN THE NEED FOR SERVICING, REPAIR OR CORRECTION OF USER EQUIPMENT OR DATA, USER ASSUMES ANY COSTS ASSOCIATED THEREWITH.

Copyright © Rockwell Automation, Inc.  All Rights Reserved. 

=============================================================
*/
public class MeterReadingPanel_RuntimeNetLogic : BaseNetLogic
{
    public override void Start()
    {
        listBoxReadings = (ListBox)Owner.GetObject("grp_MeterReading/grp_MeterReading/grp_left/ListBoxReadings"); 
        spinBox = (SpinBox)Owner.GetObject("grp_MeterReading/grp_MeterReading/grp_right/SpinBoxManualData");
        sw = (Switch)Owner.GetObject("grp_MeterReading/grp_MeterReading/grp_right/GrpSwitchManualData/Switch");
        if (listBoxReadings == null)
        {
            Log.Error("Fiix Gateway", "MeterReading Panel get ListBox error.");
            return;
        }
        GetListBoxData();
    }

    public override void Stop()
    {
        // Insert code to be executed when the user-defined logic is stopped
        if (listBoxModel != null) listBoxModel.Delete();
    }

    // Script to update selected AnalogVariable value, if the value is also binded to external source, user can add additional selection logic.
    [ExportMethod]
    public void updateSelectedVariableValue(string avName)
    {
        if (spinBox == null || sw == null || avName == null || avName == "")
        {
            Log.Error("Fiix Gateway", "MeterReading Panel get manual input data Switch and SpinBox error.");
            return;
        }
        if (!sw.Checked) return;
        AnalogItem selectedAV = avList.Find(av => av.BrowseName == avName);
        if (selectedAV != null) { 
            selectedAV.Value = spinBox.Value;
        }
        GetListBoxData();
    }
    private void GetListBoxData()
    {
        var assetNodeID = Owner.GetVariable("Asset").Value;
        IUANode assetNode = InformationModel.Get(assetNodeID);
        avList = assetNode.GetNodesByType<AnalogItem>().ToList();

        if (avList != null && avList.Count > 0)
        {
            if (listBoxModel != null) listBoxModel.Delete();
            listBoxModel = InformationModel.MakeObject("listBoxModel");
            foreach (AnalogItem av in avList)
            {
                var eu = av.EngineeringUnits;
                IUAVariable newItem = InformationModel.MakeVariable(av.BrowseName + " : " + av.Value + " " + av.EngineeringUnits.DisplayName.Text, OpcUa.DataTypes.String);
                newItem.Value = av.BrowseName;
                if (listBoxModel.Find(newItem.BrowseName) == null) listBoxModel.Add(newItem);
            }
            listBoxReadings.Model = listBoxModel.NodeId;

        // Set default value, replaced by using the Manual Data switch toggle event to update

            //if (listBoxReadings.UISelectedItem != null)
            //{
            //    IUAVariable selectedDefault = (IUAVariable)listBoxModel.Find(((LocalizedText)listBoxReadings.UISelectedValue).Text);
            //    if (selectedDefault != null) SetSpinBoxDefaultValue(selectedDefault.Value);
            //}
            
        }
    }

    [ExportMethod]
    public void SetSpinBoxDefaultValue(string avName)
    {
        if (!sw.Checked || avName == null || avName == "") return;
        AnalogItem selectedAV = avList.Find(av => av.BrowseName == avName);
        if (selectedAV != null)
        {
            spinBox.Value = selectedAV.Value;
        }
    }

    private List<AnalogItem> avList;
    private IUAObject listBoxModel;
    ListBox listBoxReadings;
    SpinBox spinBox;
    Switch sw;
}
