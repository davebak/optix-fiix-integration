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
using FTOptix.EventLogger;
using System.Runtime.CompilerServices;
#endregion
/*
Fiix Gateway runtime UI script in Home faceplate to link and update asset status to UI components.
=============================================================

Disclaimer of Warranty
THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT ARE PROVIDED "AS IS" WITHOUT WARRANTIES OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION, ALL IMPLIED WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE, NON-INFRINGEMENT OR OTHER VIOLATION OF RIGHTS. ROCKWELL AUTOMATION DOES NOT WARRANT OR MAKE ANY REPRESENTATIONS REGARDING THE USE, VALIDITY, ACCURACY, OR RELIABILITY OF, OR THE RESULTS OF ANY USE OF, OR OTHERWISE RESPECTING, THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT OR ANY WEB SITE LINKED TO THIS DOCUMENT 

Limitation of Liability
UNDER NO CIRCUMSTANCE (INCLUDING NEGLIGENCE AND TO THE FULLEST EXTEND PERMITTED BY APPLICABLE LAW) WILL ROCKWELL AUTOMATION BE LIABLE FOR ANY DIRECT, INDIRECT, SPECIAL, INCIDENTAL, PUNITIVE OR CONSEQUENTIAL DAMAGES (INCLUDING WITHOUT LIMITATION, BUSINESS INTERRUPTION, DELAYS, LOSS OF DATA OR PROFIT) ARISING OUT OF THE USE OR THE INABILITY TO USE THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT EVEN IF ROCKWELL AUTOMATION HAS BEEN ADVISED OF THE POSSIBILITY OF SUCH DAMAGES. IF USE OF SUCH MATERIALS RESULTS IN THE NEED FOR SERVICING, REPAIR OR CORRECTION OF USER EQUIPMENT OR DATA, USER ASSUMES ANY COSTS ASSOCIATED THEREWITH.

Copyright © Rockwell Automation, Inc.  All Rights Reserved. 

=============================================================
*/
public class AssetFaceplateHomeRuntimeLogic : BaseNetLogic
{
    public override void Start()
    {
        // Insert code to be executed when the user-defined logic is started
        // Pause Asset auto-update, work with UI Online/Offline switch to disable Asset update during Online status change.
        NetLogicObject runtimeLogic = (NetLogicObject)Project.Current.Find("FiixGatewayRuntimeLogic");
        if (runtimeLogic != null && runtimeLogic.GetVariable("Sts_AssetStatusUpdatePause") != null)
        {
            lastUpdatePauseSetting = runtimeLogic.GetVariable("Sts_AssetStatusUpdatePause").Value;
            runtimeLogic.GetVariable("Sts_AssetStatusUpdatePause").Value = true;
        }
        LongRunningTask updateTask = new LongRunningTask(updateAsset, LogicObject);
        updateTask.Start();
    }

    private void updateAsset()
    {
        Asset myAsset = (Asset)InformationModel.Get((NodeId)this.Owner.GetVariable("Asset").Value);
        myAsset.ExecuteMethod("UpdateRuntimeAsset");
    }

    public override void Stop()
    {
        // Insert code to be executed when the user-defined logic is stopped
        //Log.Info("Home page is closing.");

        // Resume Asset auto-update, work with UI Online/Offline switch to disable Asset update during Online status change.
        NetLogicObject runtimeLogic = (NetLogicObject)Project.Current.Find("FiixGatewayRuntimeLogic");
        if (runtimeLogic != null && runtimeLogic.GetVariable("Sts_AssetStatusUpdatePause") != null)
        {
            runtimeLogic.GetVariable("Sts_AssetStatusUpdatePause").Value = lastUpdatePauseSetting;
        }
    }

    private bool lastUpdatePauseSetting = false;
}
