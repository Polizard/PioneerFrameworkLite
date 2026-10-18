namespace PioneerFramework
{
    /// <summary>鼠标拖拽的会话状态；关闭视图时取消，释放事件不能延续至下次打开。</summary>
    internal sealed class PFViewUiDragState
    {
        public bool Orbit { get; private set; }
        public bool Pan { get; private set; }
        public bool Moved { get; private set; }
        public bool IsActive => Orbit || Pan;

        public void Begin(bool pan)
        {
            Orbit = !pan;
            Pan = pan;
            Moved = false;
        }

        public void MarkMoved() => Moved = true;

        public bool End()
        {
            bool contextClick = Orbit && !Moved;
            Reset();
            return contextClick;
        }

        public void Reset()
        {
            Orbit = Pan = Moved = false;
        }
    }
}
