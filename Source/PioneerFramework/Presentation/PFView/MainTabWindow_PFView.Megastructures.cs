using PioneerFramework.Megastructures;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    public partial class MainTabWindow_PFView
    {
        private void DrawMegastructures(CelestialBodyManager mgr,DVector3 origin)
        {
            var facilities=Current.Game?.GetComponent<MegastructureManager>();if(facilities==null)return;
            double tick=TimeScaleManager.GetPhysicsTickSafe();int frame=mgr.GetReferenceFrameId(currentSystemBodyId);
            foreach(var item in facilities.Presentations(tick))
            {
                if(item.State.FrameId!=frame)continue;
                var absolute=ViewFrame.SystemToAbsoluteForDisplay(item.State.PositionSystem,item.State.FrameId,tick,mgr);
                Vector3 position=AbsToView(absolute,origin);float size=Mathf.Max((float)(item.SizeMeters*MetersToView),position.magnitude*.003f);
                var q=item.State.Rotation;var rotation=new Quaternion((float)q.X,(float)q.Y,(float)q.Z,(float)q.W);
                matBlock.SetColor("_Color",item.Color);
                Graphics.DrawMesh(item.Mesh??cubeMesh,Matrix4x4.TRS(position,rotation,Vector3.one*size),item.Material??renderMat,SceneLayer,null,0,matBlock);
            }
        }
        private bool TrySelectMegastructure(Vector2 mouseInView,Vector2 viewSize,float nearerThan)
        {
            var facilities=Current.Game?.GetComponent<MegastructureManager>();var mgr=Current.Game?.GetComponent<CelestialBodyManager>();
            if(facilities==null||mgr==null||renderCamera==null)return false;
            double tick=TimeScaleManager.GetPhysicsTickSafe();var origin=GetRenderOrigin();float best=nearerThan;string? selected=null;
            foreach(var item in facilities.Presentations(tick))
            {
                if(item.State.FrameId!=mgr.GetReferenceFrameId(currentSystemBodyId))continue;
                Vector3 screen=renderCamera.WorldToScreenPoint(AbsToView(ViewFrame.SystemToAbsoluteForDisplay(item.State.PositionSystem,item.State.FrameId,tick,mgr),origin));if(screen.z<=0||screen.z>=best)continue;
                var point=new Vector2(screen.x/renderCamera.pixelWidth*viewSize.x,(1-screen.y/renderCamera.pixelHeight)*viewSize.y);
                if(Vector2.Distance(mouseInView,point)>25)continue;best=screen.z;selected=item.Address.InstanceId;
            }
            if(selected==null)return false;facilities.SelectedInstanceId=selected;
            QueueToast("已选中巨构（在「巨构」页签查看详情）");return true;
        }
    }
}
