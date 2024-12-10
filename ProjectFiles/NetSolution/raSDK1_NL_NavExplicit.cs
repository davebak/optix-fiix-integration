#region Using directives
using System;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.RAEtherNetIP;
using FTOptix.HMIProject;
using FTOptix.Retentivity;
using FTOptix.NativeUI;
using FTOptix.WebUI;
using FTOptix.UI;
using FTOptix.CommunicationDriver;
using FTOptix.CoreBase;
using FTOptix.NetLogic;
using FTOptix.Core;
using FTOptix.EventLogger;
#endregion

/*
Dialog box navigation script.
***** Warning *****
DO NOT EDIT!  Edits to this script may cause dialog box navigation to fail.  

=============================================================

Disclaimer of Warranty
THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT ARE PROVIDED "AS IS" WITHOUT WARRANTIES OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION, ALL IMPLIED WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE, NON-INFRINGEMENT OR OTHER VIOLATION OF RIGHTS. ROCKWELL AUTOMATION DOES NOT WARRANT OR MAKE ANY REPRESENTATIONS REGARDING THE USE, VALIDITY, ACCURACY, OR RELIABILITY OF, OR THE RESULTS OF ANY USE OF, OR OTHERWISE RESPECTING, THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT OR ANY WEB SITE LINKED TO THIS DOCUMENT 

Limitation of Liability
UNDER NO CIRCUMSTANCE (INCLUDING NEGLIGENCE AND TO THE FULLEST EXTEND PERMITTED BY APPLICABLE LAW) WILL ROCKWELL AUTOMATION BE LIABLE FOR ANY DIRECT, INDIRECT, SPECIAL, INCIDENTAL, PUNITIVE OR CONSEQUENTIAL DAMAGES (INCLUDING WITHOUT LIMITATION, BUSINESS INTERRUPTION, DELAYS, LOSS OF DATA OR PROFIT) ARISING OUT OF THE USE OR THE INABILITY TO USE THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT EVEN IF ROCKWELL AUTOMATION HAS BEEN ADVISED OF THE POSSIBILITY OF SUCH DAMAGES. IF USE OF SUCH MATERIALS RESULTS IN THE NEED FOR SERVICING, REPAIR OR CORRECTION OF USER EQUIPMENT OR DATA, USER ASSUMES ANY COSTS ASSOCIATED THEREWITH.

Copyright © Rockwell Automation, Inc.  All Rights Reserved. 

=============================================================
*/

public class raSDK1_NL_NavExplicit : BaseNetLogic
{
    public override void Start()
    {
        // Insert code to be executed when the user-defined logic is started
    }

    public override void Stop()
    {
        // Insert code to be executed when the user-defined logic is stopped
    }

    [ExportMethod]

    public void NavExplicit()
    {
        DialogType commonDb = null;
        IUAObject lButton = null;
        IUAObject launchAliasObj = null;

        try
        {
            // Get button object
            lButton = Owner.Owner.GetObject(this.Owner.BrowseName);
            // Make Launch Object that will contain aliases
            launchAliasObj = InformationModel.MakeObject("LaunchAliasObj");
        }
        catch
        {
            Log.Warning(this.GetType().Name, "Error getting owner object");
            return;
        }


        // Get each alias from Launch Button and add them into Launch Object, and assign NodeId values 
        foreach (var inpTag in lButton.Children)
        {
            if (inpTag.BrowseName.Contains("Ref_"))  // & !inpTag.BrowseName.Contains("Ref_DialogBox") & (inpTag.GetType() == typeof(UAVariable)))
            {
                // Make a variable with same name as alias of type NodeId
                var newVar = InformationModel.MakeVariable(inpTag.BrowseName, OpcUa.DataTypes.NodeId);
                try
                {
                    // Assign alias value to new variable
                    newVar.Value = ((UAManagedCore.UAVariable)inpTag).Value;
                }
                catch
                {
                    //If no value is assigned to a Ref_ input, annunciate that it is missing a node assignment
                    Log.Warning(this.GetType().Name, "Missing node assignment to variable: " + inpTag.BrowseName);
                }

                // Add variable int launch object
                launchAliasObj.Add(newVar);
            }

            else if (inpTag.BrowseName.Contains("Cfg_DialogBox"))
            {
                try
                {
                    // Assign dialog box to launch
                    commonDb = (DialogType)InformationModel.Get(((UAVariable)inpTag).Value);
                }
                catch
                {
                    //If no or bad value is assigned to Cfg_DialogBox, annunciate that dialog box is not found
                    Log.Warning(this.GetType().Name, "Unable to find Node assigned to Cfg_DialogBox");
                }
            }
        }


        // Launch the faceplate
        try
        {
            // Launch DialogBox passing Launch Object that contains the aliases as an alias 
            UICommands.OpenDialog(lButton, commonDb, launchAliasObj.NodeId);
        }
        catch
        {
            Log.Warning(this.GetType().Name, "Failed to launch dialog box specified by Cfg_DialogBox '" + commonDb.BrowseName + "'");
            return;
        }



        // If configured, close the dialog box containing launch button
        try
        {
            bool cfgCloseCurrent = lButton.GetVariable("Cfg_CloseCurrentDisplay").Value;
            if (cfgCloseCurrent)
            {
                CloseCurrentDB(Owner);
            }
        }
        catch
        {
            Log.Warning(this.GetType().Name, "Failed to close current dialog box");
        }
    }
    public void CloseCurrentDB(IUANode inputNode)
    {
        // if input node is of type Dialog, close it
        if (inputNode.GetType().BaseType.BaseType == typeof(Dialog))
        {
            // close dialog box
            ((Dialog)inputNode).Close();
            return;
        }
        // if input node is Main Window, no dialog box was found, return
        if (inputNode.GetType() == typeof(MainWindow))
        {
            return;
        }
        // continue search for Dialog or Main Window
        CloseCurrentDB(inputNode.Owner);
    }
}

