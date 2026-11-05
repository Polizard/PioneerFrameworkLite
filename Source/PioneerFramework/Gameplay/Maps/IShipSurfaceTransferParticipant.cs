namespace PioneerFramework
{
    public interface IShipSurfaceTransferParticipant
    {
        void Capture();
        void Apply();
        void Verify();
        void Rollback();
    }
}
