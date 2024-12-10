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
using FTOptix.WebUI;
using HttpAPIGateway;
using FTOptix.EventLogger;
using System.Collections.Generic;
#endregion
/*
Fiix Gateway designtime script to automate putting asset's meter readings to Push Agent's datalogger, following required naming convention. 
=============================================================

Disclaimer of Warranty
THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT ARE PROVIDED "AS IS" WITHOUT WARRANTIES OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION, ALL IMPLIED WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE, NON-INFRINGEMENT OR OTHER VIOLATION OF RIGHTS. ROCKWELL AUTOMATION DOES NOT WARRANT OR MAKE ANY REPRESENTATIONS REGARDING THE USE, VALIDITY, ACCURACY, OR RELIABILITY OF, OR THE RESULTS OF ANY USE OF, OR OTHERWISE RESPECTING, THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT OR ANY WEB SITE LINKED TO THIS DOCUMENT 

Limitation of Liability
UNDER NO CIRCUMSTANCE (INCLUDING NEGLIGENCE AND TO THE FULLEST EXTEND PERMITTED BY APPLICABLE LAW) WILL ROCKWELL AUTOMATION BE LIABLE FOR ANY DIRECT, INDIRECT, SPECIAL, INCIDENTAL, PUNITIVE OR CONSEQUENTIAL DAMAGES (INCLUDING WITHOUT LIMITATION, BUSINESS INTERRUPTION, DELAYS, LOSS OF DATA OR PROFIT) ARISING OUT OF THE USE OR THE INABILITY TO USE THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT EVEN IF ROCKWELL AUTOMATION HAS BEEN ADVISED OF THE POSSIBILITY OF SUCH DAMAGES. IF USE OF SUCH MATERIALS RESULTS IN THE NEED FOR SERVICING, REPAIR OR CORRECTION OF USER EQUIPMENT OR DATA, USER ASSUMES ANY COSTS ASSOCIATED THEREWITH.

Copyright © Rockwell Automation, Inc.  All Rights Reserved. 

=============================================================
*/
public class AssetDesignTimeLogic : BaseNetLogic
{
    [ExportMethod]
    public void AddVariablesToDataLogger()
    {
        // Add all analog variables under the asset to Gateway dedicated DataLogger for Store and Send
        // DataLogger var naming:  [AssetName]_AssetID[AssetID]_EU[EUName]_EUID[EUID]
        DataLogger dataLogger = (DataLogger)Project.Current.Find("MeterReadingDataLogger");
        AnalogItem[] variableList = LogicObject.Owner.GetNodesByType<AnalogItem>().ToArray();
        int newAV = 0, updatedAV = 0, newARP = 0, updatedARP = 0;
        string logMessage = "";

        foreach (AnalogItem item in variableList)
        {
            string varName = LogicObject.Owner.BrowseName + "_AssetID" + LogicObject.Owner.GetVariable("id").Value;
            string euName = GatewayUtils.GetEngineeringUnitNameByID(item.EngineeringUnits.UnitId);
            varName += "_EU" + euName + "_EUID" + item.EngineeringUnits.UnitId;
            bool found = false;
            foreach (VariableToLog var in dataLogger.VariablesToLog)
            {
                if (var.BrowseName == varName)
                {
                    found = true;
                    var.SetDynamicLink(item, DynamicLinkMode.Read);
                    var.DeadBandMode = dataLogger.DefaultDeadBandMode;
                    var.DeadBandValue = dataLogger.DefaultDeadBandValue;
                    updatedAV++;
                    break;
                }
            }

            if (!found)
            {
                var newVAR = InformationModel.MakeVariable<VariableToLog>(varName, OpcUa.DataTypes.Float);
                newVAR.SetDynamicLink(item, DynamicLinkMode.Read);
                newVAR.DeadBandMode = dataLogger.DefaultDeadBandMode;
                newVAR.DeadBandValue = dataLogger.DefaultDeadBandValue;
                dataLogger.VariablesToLog.Add(newVAR);
                newAV++;
            }
        }
        logMessage = "For asset " + LogicObject.Owner.BrowseName + ", " + newAV + " new variables added " + updatedAV + " updated; ";

        // Adding ARP function 07/2024, scan ARPData children and add them to Datalogger if found
        List<IUANode> allChildren = LogicObject.Owner.Children.Cast<IUANode>().ToList();
        foreach (IUANode child in allChildren)
        {
            // Check if the child is a ARPData object, form variable to log name as [AssetName]_AssetID[AssetID]_EU[EU]__[ARPDataName]_EUID[EUID]_FiixARP
            // Caution: require no __ in EU name
            if (child.NodeClass == NodeClass.Object && child.GetVariable("Cfg_RawDataSend_enabled")!=null && child.GetVariable("dataReading")!=null)
            {
                AnalogItem item = (AnalogItem)child.GetVariable("dataReading");
                string varName = LogicObject.Owner.BrowseName + "_AssetID" + LogicObject.Owner.GetVariable("id").Value;
                // embed ARPData name to EUname, to be used when compose JSON payload as part of the SensorID. (for case of multiple ARPs use the same EU)
                string euName = GatewayUtils.GetEngineeringUnitNameByID(item.EngineeringUnits.UnitId);
                varName += "_EU" + euName + "__" + child.BrowseName + "_EUID" + item.EngineeringUnits.UnitId;
                // JSON output is always enabled to send, other context data is not sent as no EU 
                string jsonName = varName + "_FiixARP1_JSON";
                string enabledName = varName + "_FiixARP0_enabled";
                string recipeName = varName + "_FiixARP0_strRecipe";
                string faultName = varName + "_FiixARP0_strFault";
                string messageName = varName + "_FiixARP0_strMessage";
                string runningName = varName + "_FiixARP0_bolIsMachineRunning";

                varName += "_FiixARP" + (child.GetVariable("Cfg_RawDataSend_enabled").Value ? "1" : "0");

                bool foundVar = false, foundJson = false, foundEnabled = false, foundRecipe = false, foundFault = false, foundMessage = false, foundRunning = false;
                foreach (VariableToLog var in dataLogger.VariablesToLog)
                {
                    if (var.BrowseName == varName)
                    {
                        foundVar = true;
                        var.SetDynamicLink(item, DynamicLinkMode.Read);
                        var.DeadBandMode = dataLogger.DefaultDeadBandMode;
                        var.DeadBandValue = dataLogger.DefaultDeadBandValue;
                        updatedARP++;
                    }
                    if (var.BrowseName == jsonName)
                    {
                        foundJson = true;
                        var.SetDynamicLink(child.GetVariable("Out_JSON"), DynamicLinkMode.Read);
                        var.DeadBandMode = DeadBandMode.None;
                        var.DeadBandValue = dataLogger.DefaultDeadBandValue;
                    }
                    if (var.BrowseName == enabledName)
                    {
                        foundEnabled = true;
                        var.SetDynamicLink(child.GetVariable("Set_ARPSend_enabled"), DynamicLinkMode.Read);
                        var.DeadBandMode = DeadBandMode.None;
                        var.DeadBandValue = dataLogger.DefaultDeadBandValue;
                    }
                    if (var.BrowseName == recipeName)
                    {
                        foundRecipe = true;
                        var.SetDynamicLink(child.GetVariable("strRecipe"), DynamicLinkMode.Read);
                        var.DeadBandMode = DeadBandMode.None;
                        var.DeadBandValue = dataLogger.DefaultDeadBandValue;
                    }
                    if (var.BrowseName == faultName)
                    {
                        foundFault = true;
                        var.SetDynamicLink(child.GetVariable("strFault"), DynamicLinkMode.Read);
                        var.DeadBandMode = DeadBandMode.None;
                        var.DeadBandValue = dataLogger.DefaultDeadBandValue;
                    }
                    if (var.BrowseName == runningName)
                    {
                        foundRunning = true;
                        var.SetDynamicLink(child.GetVariable("bolIsMachineRunning"), DynamicLinkMode.Read);
                        var.DeadBandMode = DeadBandMode.None;
                        var.DeadBandValue = dataLogger.DefaultDeadBandValue;
                    }
                    if (var.BrowseName == messageName)
                    {
                        foundMessage = true;
                        var.SetDynamicLink(child.GetVariable("strMessage"), DynamicLinkMode.Read);
                        var.DeadBandMode = DeadBandMode.None;
                        var.DeadBandValue = dataLogger.DefaultDeadBandValue;
                    }
                } // Finish looping existing DataLogger's variable list

                if (!foundVar)
                {
                    var newVAR = InformationModel.MakeVariable<VariableToLog>(varName, OpcUa.DataTypes.Float);
                    newVAR.SetDynamicLink(item, DynamicLinkMode.Read);
                    newVAR.DeadBandMode = dataLogger.DefaultDeadBandMode;
                    newVAR.DeadBandValue = dataLogger.DefaultDeadBandValue;
                    dataLogger.VariablesToLog.Add(newVAR);
                    newARP++;
                }
                if (!foundJson)
                {
                    var newVAR = InformationModel.MakeVariable<VariableToLog>(jsonName, OpcUa.DataTypes.String);
                    newVAR.SetDynamicLink(child.GetVariable("Out_JSON"), DynamicLinkMode.Read);
                    newVAR.DeadBandMode = DeadBandMode.None;
                    newVAR.DeadBandValue = dataLogger.DefaultDeadBandValue;
                    dataLogger.VariablesToLog.Add(newVAR);
                }
                if (!foundEnabled)
                {
                    var newVAR = InformationModel.MakeVariable<VariableToLog>(enabledName, OpcUa.DataTypes.Boolean);
                    newVAR.SetDynamicLink(child.GetVariable("Set_ARPSend_enabled"), DynamicLinkMode.Read);
                    newVAR.DeadBandMode = DeadBandMode.None;
                    newVAR.DeadBandValue = dataLogger.DefaultDeadBandValue;
                    dataLogger.VariablesToLog.Add(newVAR);
                }
                if (!foundRecipe)
                {
                    var newVAR = InformationModel.MakeVariable<VariableToLog>(recipeName, OpcUa.DataTypes.String);
                    newVAR.SetDynamicLink(child.GetVariable("strRecipe"), DynamicLinkMode.Read);
                    newVAR.DeadBandMode = DeadBandMode.None;
                    newVAR.DeadBandValue = dataLogger.DefaultDeadBandValue;
                    dataLogger.VariablesToLog.Add(newVAR);
                }
                if (!foundRunning)
                {
                    var newVAR = InformationModel.MakeVariable<VariableToLog>(runningName, OpcUa.DataTypes.Boolean);
                    newVAR.SetDynamicLink(child.GetVariable("bolIsMachineRunning"), DynamicLinkMode.Read);
                    newVAR.DeadBandMode = DeadBandMode.None;
                    newVAR.DeadBandValue = dataLogger.DefaultDeadBandValue;
                    dataLogger.VariablesToLog.Add(newVAR);
                }
                if (!foundFault)
                {
                    var newVAR = InformationModel.MakeVariable<VariableToLog>(faultName, OpcUa.DataTypes.String);
                    newVAR.SetDynamicLink(child.GetVariable("strFault"), DynamicLinkMode.Read);
                    newVAR.DeadBandMode = DeadBandMode.None;
                    newVAR.DeadBandValue = dataLogger.DefaultDeadBandValue;
                    dataLogger.VariablesToLog.Add(newVAR);
                }
                if (!foundMessage)
                {
                    var newVAR = InformationModel.MakeVariable<VariableToLog>(messageName, OpcUa.DataTypes.String);
                    newVAR.SetDynamicLink(child.GetVariable("strMessage"), DynamicLinkMode.Read);
                    newVAR.DeadBandMode = DeadBandMode.None;
                    newVAR.DeadBandValue = dataLogger.DefaultDeadBandValue;
                    dataLogger.VariablesToLog.Add(newVAR);
                }
            } // finish qualified child process
        } // finish children looping
        logMessage += newARP + " new ARP Data added " + updatedARP + " updated to MeterReadingDataLogger.";
        Log.Info("AssetDesigntimeLogic", logMessage);
    }
}
