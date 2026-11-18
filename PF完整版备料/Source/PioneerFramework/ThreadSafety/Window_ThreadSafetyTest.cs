using System.Linq;
using UnityEngine;
using Verse;

namespace PioneerFramework.ThreadSafety
{
    /// <summary>
    /// 多线程安全检测调试窗口。
    /// 在开发模式下通过 Debug Actions 菜单打开。
    /// </summary>
    public class Window_ThreadSafetyTest : Window
    {
        private Vector2 _scrollPos = Vector2.zero;
        private FullTestReport _lastReport;
        private string _statusMessage = "就绪。点击按钮运行测试。";
        private bool _isRunning;

        // 可调参数
        private int _threadCount = 8;
        private int _determinismIterations = 1000;
        private int _stressDurationSeconds = 5;
        private bool _runSmokeOnly;

        public override Vector2 InitialSize => new Vector2(700f, 550f);

        public Window_ThreadSafetyTest()
        {
            doCloseButton = true;
            draggable = true;
            resizeable = true;
            forcePause = false;
            absorbInputAroundWindow = false;
        }

        public override void DoWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);

            // ── 标题 ──
            Text.Font = GameFont.Medium;
            listing.Label("多线程安全检测");
            Text.Font = GameFont.Small;

            listing.GapLine();
            listing.Gap(4f);

            // ── 参数设置 ──
            listing.Label("测试参数:");
            listing.Gap(2f);

            // 线程数
            Rect threadRow = listing.GetRect(24f);
            Widgets.Label(threadRow.LeftHalf(), "线程数:");
            string threadStr = Widgets.TextField(threadRow.RightHalf(), _threadCount.ToString());
            int.TryParse(threadStr, out _threadCount);
            _threadCount = Mathf.Clamp(_threadCount, 1, 64);

            // 确定性迭代次数
            Rect detRow = listing.GetRect(24f);
            Widgets.Label(detRow.LeftHalf(), "确定性迭代:");
            string detStr = Widgets.TextField(detRow.RightHalf(), _determinismIterations.ToString());
            int.TryParse(detStr, out _determinismIterations);
            _determinismIterations = Mathf.Clamp(_determinismIterations, 100, 100000);

            // 压力测试时长
            Rect stressRow = listing.GetRect(24f);
            Widgets.Label(stressRow.LeftHalf(), "压力测试时长(s):");
            string stressStr = Widgets.TextField(stressRow.RightHalf(), _stressDurationSeconds.ToString());
            int.TryParse(stressStr, out _stressDurationSeconds);
            _stressDurationSeconds = Mathf.Clamp(_stressDurationSeconds, 1, 60);

            listing.Gap(4f);

            // ── 模式选择 ──
            Widgets.CheckboxLabeled(listing.GetRect(24f), "仅冒烟测试（跳过性能和压力测试）", ref _runSmokeOnly);

            listing.Gap(8f);

            // ── 按钮 ──
            bool canRun = !_isRunning;
            if (canRun)
            {
                if (listing.ButtonText("运行全部测试"))
                {
                    RunTests();
                }
            }
            else
            {
                listing.Label("测试运行中...");
            }

            listing.Gap(4f);

            // ── 状态 ──
            listing.Label("状态: " + _statusMessage);

            listing.GapLine();
            listing.Gap(4f);

            // ── 结果展示 ──
            if (_lastReport != null)
            {
                Text.Font = GameFont.Medium;
                listing.Label("测试结果");
                Text.Font = GameFont.Small;

                listing.Gap(4f);

                // 总体统计
                Color oldColor = GUI.color;
                GUI.color = _lastReport.TotalFailed > 0 ? Color.red : Color.green;
                listing.Label(string.Format("  {0}/{1} 通过, {2:F0}ms",
                    _lastReport.TotalPassed, _lastReport.TotalCases, _lastReport.TotalElapsedMs));
                GUI.color = oldColor;

                listing.Gap(4f);

                // 滚动区域：各套件详情
                Rect scrollRect = listing.GetRect(280f);
                Rect scrollView = new Rect(0, 0, scrollRect.width - 20f, _lastReport.Suites.Count * 120f);
                Widgets.BeginScrollView(scrollRect, ref _scrollPos, scrollView);

                float y = 0;
                foreach (var suite in _lastReport.Suites)
                {
                    Rect suiteRect = new Rect(0, y, scrollView.width, 110f);
                    Widgets.DrawBox(suiteRect);

                    Color suiteColor = suite.FailedCount > 0 ? Color.red : Color.green;
                    GUI.color = suiteColor;
                    Widgets.Label(new Rect(4, y + 2, scrollView.width - 8, 20), suite.SuiteName);
                    GUI.color = oldColor;

                    Widgets.Label(new Rect(4, y + 22, scrollView.width - 8, 20),
                        string.Format("  {0}/{1} 通过 ({2:F0}ms)",
                            suite.PassedCount, suite.Cases.Count, suite.TotalElapsedMs));

                    float caseY = y + 42;
                    foreach (var c in suite.Cases)
                    {
                        Color caseColor = c.Passed ? Color.green : Color.red;
                        GUI.color = caseColor;
                        string status = c.Passed ? "O" : "X";
                        Widgets.Label(new Rect(8, caseY, scrollView.width - 16, 18),
                            string.Format("  [{0}] {1} ({2:F0}ms)", status, c.Name, c.ElapsedMs));
                        GUI.color = oldColor;

                        if (!string.IsNullOrEmpty(c.Message))
                        {
                            Widgets.Label(new Rect(24, caseY + 16, scrollView.width - 32, 18), c.Message);
                            caseY += 18;
                        }
                        caseY += 22;
                    }

                    y += 115;
                }

                Widgets.EndScrollView();
            }

            listing.End();
        }

        private void RunTests()
        {
            _isRunning = true;
            _statusMessage = "测试运行中...";

            LongEventHandler.ExecuteWhenFinished(() =>
            {
                try
                {
                    if (_runSmokeOnly)
                    {
                        _lastReport = ThreadSafetyTestRunner.RunSmoke(_threadCount);
                    }
                    else
                    {
                        _lastReport = ThreadSafetyTestRunner.RunAll(
                            _threadCount, _determinismIterations, _stressDurationSeconds);
                    }

                    _statusMessage = ThreadSafetyTestRunner.GetSummary(_lastReport);
                    ThreadSafetyTestRunner.RunAllAndLog(
                        _threadCount, _determinismIterations, _stressDurationSeconds);
                }
                catch (System.Exception ex)
                {
                    _statusMessage = "测试异常: " + ex.Message;
                    Log.Error("[ThreadSafety] 测试运行异常: " + ex);
                }
                finally
                {
                    _isRunning = false;
                }
            });
        }
    }
}