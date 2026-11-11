using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using PioneerFramework.Combat;
using Xunit;

namespace PioneerFramework.Tests
{
    public class PFViewUiIntegrationTests
    {
        private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

        [Theory]
        [InlineData("DrawInspector")]
        [InlineData("DrawRadarPanel")]
        public void ObservedContactHasExplicitTargetAction(string method)
        {
            Assert.Contains(ReferencedMembers(typeof(MainTabWindow_PFView).GetMethod(method, Flags)),
                member => member.Name == "SetTargetContact");
        }

        // These guards exercise compiled routing boundaries; rendering/input still require the game.
        [Fact]
        public void WeaponSubmissionUsesExplicitSourceAndRevalidatesBeforeHostDispatch()
        {
            var method = typeof(MainTabWindow_PFView).GetMethod("TryFireAtTarget", Flags);
            Assert.NotNull(method);
            Assert.Equal(typeof(int), method.GetParameters()[0].ParameterType);
            var members = ReferencedMembers(method).ToArray();
            Assert.DoesNotContain(members, m => m is FieldInfo && m.Name == "selectedGroupId");
            Assert.Contains(members, m => m.DeclaringType == typeof(PFViewUiOperationContext) && m.Name == "CanFire");
            Assert.Contains(members, m => m.DeclaringType == typeof(CombatManager) && m.Name == "GetGroupWeapons");
            Assert.Contains(members, m => m.DeclaringType == typeof(PioneerFramework.SpaceAI.ContactTargetService) && m.Name == "TryFire");
            // 批 5e §4.3：挂载点解析下移到 ContactTargetService 的 id 重载——"落点前复核 + 宿主派发"仍在同一条链上。
            var dispatch = typeof(PioneerFramework.SpaceAI.ContactTargetService).GetMethod("TryFire", Flags, null,
                new[] { typeof(SensorManager), typeof(CombatManager), typeof(int), typeof(WeaponInstance),
                    typeof(PioneerFramework.SpaceAI.ContactTarget) }, null);
            Assert.NotNull(dispatch);
            Assert.Contains(ReferencedMembers(dispatch), m => m.DeclaringType == typeof(CombatManager) && m.Name == "TryGetMapWeaponMount");
        }

        [Fact]
        public void ClearingInspectionCannotCloseSteeringOrChangeCameraFocus()
        {
            var method = typeof(MainTabWindow_PFView).GetMethod("ClearInspection", Flags);
            Assert.NotNull(method);
            var members = ReferencedMembers(method).ToArray();
            Assert.DoesNotContain(members, m => m.DeclaringType == typeof(SteeringController));
            Assert.DoesNotContain(members, m => m.Name == "FocusGroup" || m.Name == "FocusBody");
            Assert.DoesNotContain(members, WritesWeaponTarget);
        }

        [Theory]
        [InlineData("DrawRadarPanel")]
        [InlineData("DrawNavballPanel")]
        public void InstrumentsUsePassedOperationSourceInsteadOfInspection(string name)
        {
            // 批 5e §4.3：仪表面板改吃组 id（不再传引擎对象），"用传入源、不读 selectedGroupId" 的判据不变。
            var method = typeof(MainTabWindow_PFView).GetMethod(name, Flags, null,
                new[] { typeof(int) }, null);
            Assert.NotNull(method);
            Assert.DoesNotContain(ReferencedMembers(method), m => m is FieldInfo && m.Name == "selectedGroupId");
        }

        [Fact]
        public void ViewingAGroupNeverChangesFocusOrWeaponTarget()
        {
            var method = typeof(MainTabWindow_PFView).GetMethod("SelectGroupForInspection", Flags);
            Assert.NotNull(method);
            var members = ReferencedMembers(method).ToArray();
            Assert.DoesNotContain(members, m => m.Name == "FocusGroup" || m.Name == "FocusBody");
            Assert.DoesNotContain(members, WritesWeaponTarget);
        }

        [Fact]
        public void DragReleaseIsProcessedBeforeViewportRejection()
        {
            var method = typeof(MainTabWindow_PFView).GetMethod("HandleInput", Flags);
            var calls = ReferencedMembers(method).OfType<MethodBase>().Select(m => m.Name).ToList();
            Assert.True(calls.IndexOf("EndCameraDrag") >= 0);
            Assert.True(calls.IndexOf("EndCameraDrag") < calls.IndexOf("MouseInViewport"));
        }

        [Fact]
        public void CapturedCameraDragContinuesBeforeSidePanelsRejectNewInput()
        {
            var method = typeof(MainTabWindow_PFView).GetMethod("HandleInput", Flags);
            var calls = ReferencedMembers(method).OfType<MethodBase>().Select(m => m.Name).ToList();
            Assert.True(calls.IndexOf("HandleCameraDrag") >= 0);
            Assert.True(calls.IndexOf("HandleCameraDrag") < calls.IndexOf("MouseInViewport"));
        }

        [Fact]
        public void LayoutChangesCannotResizeCameraOrRenderTexture()
        {
            var method = typeof(MainTabWindow_PFView).GetMethod("ComputeLayout", Flags);
            Assert.DoesNotContain(ReferencedMembers(method), m => m.DeclaringType == typeof(UnityEngine.Camera)
                || m.DeclaringType == typeof(UnityEngine.RenderTexture));
        }
        [Theory]
        [InlineData("TryPickGroup")]
        [InlineData("DrawSystemGroups")]
        public void EntityProjectionChecksKnowledgeBeforeReadingTruePosition(string name)
        {
            var method = typeof(MainTabWindow_PFView).GetMethod(name, Flags);
            var calls = ReferencedMembers(method).OfType<MethodBase>().Select(m => m.Name).ToList();
            // 批 5e §4.3：准入判据从引擎对象版改为 id 版（同一个 UI 策略），顺序判据不变。
            Assert.True(calls.IndexOf("CanInspectGroup") >= 0);
            Assert.True(calls.IndexOf("CanInspectGroup") < calls.IndexOf("GetGroupPosAbs"));
        }

        [Fact]
        public void ViewRestoreValidatesSnapshotWithoutCreatingReturnLoop()
        {
            var method = typeof(MainTabWindow_PFView).GetMethod("RestorePreviousView", Flags);
            Assert.NotNull(method);
            var members = ReferencedMembers(method).ToArray();
            Assert.Contains(members, m => m.Name == "IsPreviousViewValid");
            Assert.DoesNotContain(members, m => m.Name == "RememberPreviousView");
        }

        [Fact]
        public void WorldChangeClearsPreviousViewHistory()
        {
            var method = typeof(MainTabWindow_PFView).GetMethod("ResolveUiContext", Flags);
            Assert.Contains(ReferencedMembers(method), m => m.Name == "Clear"
                && m.DeclaringType.Name.StartsWith("PFViewUiViewHistory", StringComparison.Ordinal));
        }
        [Fact]
        public void SnapshotRefreshDropsOldRowsBeforeReadingNewWorldComponents()
        {
            var members = ReferencedMembers(typeof(MainTabWindow_PFView).GetMethod("RefreshObjectRows", Flags)).ToList();
            int clear = -1;
            for (int i = 0; i + 1 < members.Count; i++)
                if (members[i].Name == "filteredObjectRows" && members[i + 1].Name == "Clear") { clear = i; break; }
            Assert.True(clear >= 0);
            Assert.True(clear < members.FindIndex(m => m.Name == "GetComponent"));
        }

        [Fact]
        public void SteeringPausesHeldKeysBeforePollingWhileTextOrPopupOwnsKeyboard()
        {
            var calls = ReferencedMembers(typeof(Window_Steering).GetMethod("ReadKeys", Flags)).ToList();
            Assert.Contains(calls, m => m.Name == "CanReadKeyboard");
            Assert.Contains(calls, m => m.Name == "Poll");
            Assert.True(calls.FindIndex(m => m.Name == "CanReadKeyboard") < calls.FindIndex(m => m.Name == "Poll"));
            Assert.Contains(calls, m => m.Name == "ClearHeldKeys");
            var poll = typeof(SteeringInputHost).GetMethod("Poll", Flags);
            Assert.Contains(ReferencedMembers(poll), m => m.Name == "ProcessSnapshot");
            var gate = typeof(Window_Steering).GetMethod("CanReadKeyboard", Flags);
            Assert.Contains(ReferencedMembers(gate), m => m.Name == "get_HasTextInputFocus");
            Assert.Contains(ReferencedMembers(gate), m => m.Name == "get_Windows");
        }

        [Fact]
        public void ClearingHeldKeysPreservesOperationSourceAndSas()
        {
            var clear = typeof(Window_Steering).GetMethod("ClearHeldKeys", Flags);
            Assert.NotNull(clear);
            SteeringController.Open(73);
            SteeringController.SasKind = SasTargetKind.Prograde;
            var keys = typeof(SteeringController).GetFields(Flags).Where(f => f.Name.StartsWith("Key", StringComparison.Ordinal)).ToArray();
            try
            {
                foreach (var key in keys) key.SetValue(null, true);
                clear.Invoke(null, null);
                Assert.All(keys, key => Assert.False((bool)key.GetValue(null)));
                Assert.Equal(73, SteeringController.GroupId);
                Assert.Equal(SasTargetKind.Prograde, SteeringController.SasKind);
            }
            finally { SteeringController.Close(); }
        }

        [Fact]
        public void SystemNavigationDoesNotChangeSystemBeforeFocusCapturesHistory()
        {
            var callbacks = typeof(MainTabWindow_PFView).GetNestedTypes(Flags).SelectMany(t => t.GetMethods(Flags))
                .Concat(typeof(MainTabWindow_PFView).GetMethods(Flags))
                .Where(m => m.Name.Contains("<ShowSystemMenu>", StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(callbacks);
            var callback = Assert.Single(callbacks.Where(m => ReferencedMembers(m).Any(r => r.Name == "FocusBody")));
            Assert.DoesNotContain(ReferencedMembers(callback), m => m is FieldInfo && m.Name == "currentSystemBodyId");
        }

        [Fact]
        public void DisplayMenuAlwaysOffersFullNavballReadouts()
        {
            var callbacks = typeof(MainTabWindow_PFView).GetNestedTypes(Flags).SelectMany(t => t.GetMethods(Flags))
                .Concat(typeof(MainTabWindow_PFView).GetMethods(Flags))
                .Where(m => m.Name.Contains("<ShowDisplayMenu>", StringComparison.Ordinal));
            Assert.Contains(callbacks, m => ReferencedMembers(m).Any(r => r.Name == "OpenInstrumentWindow"));
            Assert.Contains(ReferencedMembers(typeof(Window_PFViewInstrument).GetMethod("DoWindowContents", Flags)),
                m => m.Name == "BeginScrollView");
        }

        [Fact]
        public void SessionResetClearsInterruptedDragsWithoutChangingCameraPose()
        {
            var reset = typeof(MainTabWindow_PFView).GetMethod("ResetCameraDrag", Flags);
            Assert.NotNull(reset);
            // Do not instantiate a GPU-backed window in testhost. Exercise its drag state in Pure.Tests,
            // and verify this lifecycle route invokes that same reset without touching camera pose.
            var calls = ReferencedMembers(reset).ToArray();
            Assert.Contains(calls, m => m.Name == "Reset" && m.DeclaringType.Name == "PFViewUiDragState");
            Assert.DoesNotContain(calls, m => m is FieldInfo && m.Name.StartsWith("cam", StringComparison.Ordinal));
            Assert.Contains(ReferencedMembers(typeof(MainTabWindow_PFView).GetMethod("ResetUiSession", Flags)), m => m.Name == "ResetCameraDrag");
        }

        [Fact]
        public void KnownEntitiesOutsideRegionCannotBecomeUnidentifiedContacts()
        {
            var members = ReferencedMembers(typeof(MainTabWindow_PFView).GetMethod("RefreshObjectRows", Flags)).ToList();
            int admission = members.FindIndex(m => m.Name == "CanInspectGroup");
            int knownSet = members.FindIndex(Math.Max(0, admission), m => m.Name == "knownEntityIds");
            int region = members.FindIndex(m => m.Name == "Contains" && m.DeclaringType == typeof(HashSet<int>));
            Assert.True(admission >= 0 && knownSet > admission && region > knownSet);
        }

        [Fact]
        public void SteeringShutdownCapturesMapBeforeClearingSource()
        {
            var stop = typeof(Window_Steering).GetMethod("StopControl", Flags);
            Assert.NotNull(stop);
            var members = ReferencedMembers(stop).ToList();
            Assert.True(members.FindIndex(m => m.Name == "GetCurrentMapComp") < members.FindIndex(m => m.Name == "Close"));
            Assert.Contains(members, m => m.Name == "SetThrustersBurning");
            Assert.Contains(ReferencedMembers(typeof(MainTabWindow_PFView).GetMethod("EndSteering", Flags)), m => m.Name == "StopControl");
        }

        [Theory]
        [InlineData(typeof(MainTabWindow_PFView), "ToggleManualSteering")]
        [InlineData(typeof(CompSteeringConsole), "OpenSteeringWindow")]
        public void SwitchingControlStopsTheOldSessionBeforeOpeningTheNewOne(Type owner, string method)
        {
            var calls = ReferencedMembers(owner.GetMethod(method, Flags)).ToList();
            int stop = calls.FindIndex(m => m.Name == "StopControl");
            int open = calls.FindIndex(m => m.DeclaringType == typeof(SteeringController) && m.Name == "Open");
            Assert.True(stop >= 0 && open > stop);
        }

        [Fact]
        public void RefocusingAMemorizedBodyAppliesCurrentOptionsAfterLoadingItsPose()
        {
            var calls = ReferencedMembers(typeof(MainTabWindow_PFView).GetMethod("FocusBody", Flags)).ToList();
            int load = calls.FindIndex(m => m.Name == "TryLoadPose");
            int refresh = calls.FindLastIndex(m => m.Name == "RefreshPilotCameraOptions");
            Assert.True(load >= 0 && refresh > load);
        }

        [Theory]
        [InlineData(typeof(MainTabWindow_PFView))]
        [InlineData(typeof(Window_Steering))]
        public void ViewAndSteeringCannotSelectAGameSpeed(Type window)
        {
            foreach (var method in window.GetMethods(Flags | BindingFlags.DeclaredOnly))
            {
                Assert.DoesNotContain(ReferencedMembers(method), member =>
                    member.DeclaringType == typeof(TimeScaleManager) && member.Name == nameof(TimeScaleManager.SetGameSpeed)
                    || member.DeclaringType == typeof(Verse.TickManager) && member.Name == "set_CurTimeSpeed"
                    || member.Name.StartsWith("TimeSpeed_", StringComparison.Ordinal));
            }
        }

        [Fact]
        public void BothInputOwnersDispatchTheSameCameraShortcuts()
        {
            foreach (var pair in new[] { (typeof(MainTabWindow_PFView), "HandleInput"), (typeof(Window_Steering), "ReadKeys") })
                Assert.Contains(ReferencedMembers(pair.Item1.GetMethod(pair.Item2, Flags)), m => m.Name == "HandlePilotCameraShortcut");
        }

        [Fact]
        public void InspectionLossExplainsCleanupWithoutChangingOtherSubjects()
        {
            var lost = typeof(MainTabWindow_PFView).GetMethod("InvalidateInspection", Flags);
            Assert.NotNull(lost);
            var members = ReferencedMembers(lost).ToArray();
            Assert.Contains(members, m => m.Name == "ClearInspection");
            Assert.Contains(members, m => m.Name == "QueueToast");
            Assert.DoesNotContain(members, m => m.DeclaringType == typeof(SteeringController)
                || m.Name == "FocusGroup" || WritesWeaponTarget(m));
            Assert.Contains(ReferencedMembers(typeof(MainTabWindow_PFView).GetMethod("ResolveUiContext", Flags)), m => m.Name == "InvalidateInspection");
        }

        [Fact]
        public void ContactDetailsUseObservedCoordinatesAndFreshnessWithoutEntityIdentity()
        {
            var details = typeof(MainTabWindow_PFView).GetMethod("DrawContactDetails", Flags);
            Assert.NotNull(details);
            var members = ReferencedMembers(details).ToArray();
            Assert.Contains(members, m => m.DeclaringType == typeof(Contact) && m.Name == "position");
            Assert.Contains(members, m => m.Name == "ObservationIsCurrent");
            Assert.DoesNotContain(members, m => m.DeclaringType == typeof(SpaceEntityGroup)
                || m.Name == "get_IsForged" || m.Name == "targetGroupId" || m.Name == "decoyId");
        }

        [Fact]
        public void CameraInputAndRenderingApplyFocusAwarePitchAndContinuousOrbitUp()
        {
            var input = ReferencedMembers(typeof(MainTabWindow_PFView).GetMethod("HandleInput", Flags)).ToArray();
            Assert.Contains(input, m => m.Name == "HandleCameraDrag");
            var drag = ReferencedMembers(typeof(MainTabWindow_PFView).GetMethod("HandleCameraDrag", Flags)).ToArray();
            Assert.Contains(drag, m => m.DeclaringType == typeof(PFViewOrbitDragState) && m.Name == "AddDrag");
            var bounds = ReferencedMembers(typeof(PFViewOrbitDragState).GetMethod("AddDrag", Flags)).ToArray();
            Assert.Contains(bounds, m => m.DeclaringType == typeof(PFViewCameraCore) && m.Name == "ResolvePitch");
            Assert.Contains(bounds, m => m.DeclaringType == typeof(PFViewPilotCameraCore) && m.Name == "LookPitch");
            foreach (var stage in new[] { input, drag, bounds })
                Assert.DoesNotContain(stage, m => m.DeclaringType == typeof(PFViewCameraCore) && m.Name == "ClampPitch");
            var render = ReferencedMembers(typeof(MainTabWindow_PFView).GetMethod("UpdateCamera", Flags)).ToArray();
            Assert.Contains(render, m => m.DeclaringType == typeof(PFViewCameraCore) && m.Name == "ResolvePitch");
            Assert.Contains(render, m => m.DeclaringType == typeof(PFViewCameraCore) && m.Name == "OrbitUp");
            Assert.Contains(render, m => m.DeclaringType == typeof(PFViewCameraCore) && m.Name == "InterpolateOrbit");
        }

        private static bool WritesWeaponTarget(MemberInfo member)
            => member.Name == "set_targetGroupId"
               || member is FieldInfo && (member.Name == "targetGroupId" || member.Name == "weaponTarget");

        internal static IEnumerable<MemberInfo> ReferencedMembers(MethodBase method)
        {
            var bytes = method.GetMethodBody()?.GetILAsByteArray() ?? Array.Empty<byte>();
            var codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null))
                .ToDictionary(c => unchecked((ushort)c.Value));
            for (int i = 0; i < bytes.Length;)
            {
                ushort value = bytes[i++];
                if (value == 0xfe) value = (ushort)(0xfe00 | bytes[i++]);
                var code = codes[value];
                if (code.OperandType is OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineTok)
                    yield return method.Module.ResolveMember(BitConverter.ToInt32(bytes, i),
                        method.DeclaringType?.GetGenericArguments(), method.IsGenericMethod ? method.GetGenericArguments() : null);
                i += code.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, i),
                    _ => 4
                };
            }
        }
    }
}
