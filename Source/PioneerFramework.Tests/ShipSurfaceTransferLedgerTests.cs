using PioneerFramework;
using Xunit;

namespace PioneerFramework.Tests
{
    public class ShipSurfaceTransferLedgerTests
    {
        [Fact]
        public void CompletedOperationCannotStartAgain()
        {
            var ledger = new ShipSurfaceTransferLedger(null);
            Assert.True(ledger.TryBegin("landing-42"));
            ledger.Complete("landing-42");
            Assert.True(ledger.IsCompleted("landing-42"));
            Assert.False(ledger.TryBegin("landing-42"));
        }

        [Fact]
        public void CancelledOperationMayRetry()
        {
            var ledger = new ShipSurfaceTransferLedger(null);
            Assert.True(ledger.TryBegin("landing-42"));
            ledger.Cancel("landing-42");
            Assert.True(ledger.TryBegin("landing-42"));
        }

        [Fact]
        public void BlockedOperationCannotRetry()
        {
            var ledger = new ShipSurfaceTransferLedger(null);
            Assert.True(ledger.TryBegin("landing-42"));
            ledger.Block("landing-42");
            Assert.True(ledger.IsBlocked("landing-42"));
            Assert.False(ledger.TryBegin("landing-42"));
        }
    }
}
