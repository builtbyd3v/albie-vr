# Quest workflow connectivity repair

Status: source fix and cloud-only Unity regression verified; physical Quest and hosted production service not yet verified.

## Problems found

- Project workflow and Agent office required ForgeNet to be Live, even when ForgeCloud was Online. The cloud already
  supports device-scoped build tools, but these panels only used the laptop's /agent/builds REST endpoints.
- Opening New task during the initial status request could leave Create implementation plan disabled after the
  request completed. Background polling also shared the action lock, so slow status requests could swallow approval taps.
- The on-device cloud tool catalog omitted all six build tools, leaving voice without the workflow capabilities.
- Office status fetch errors were silently ignored.
- This checkout has neither a saved nor bundled cloud app key. A new build needs the existing service's ALBIE_APP_KEY
  in the ignored Resources/AlbieAppKey.txt file (or the saved Albie.AppKey preference). Never commit the key or paste it
  into a chat. The installed headset's configuration cannot be inferred from the local checkout.

## Changes

OSWorkflowTransport selects the existing hub REST routes or ForgeCloud.CallTool. Cloud calls retain the service's
x-albie-device scoping and use its existing build_start/status/approve/review/cancel/ship tools. No new public API or
global build state is added. Cloud publication keeps the two-call confirmation; the second UI action is explicitly
labeled Confirm publish and open PR.

The form updates action availability without rebuilding focused text fields. Status polling has its own lock and
waits three seconds after completion, leaving actions responsive on slow connections. The cloud voice catalog now
includes workflow tools, with explicit plan and publication approval instructions. A successful voice build_start
opens Project workflow. Office errors and missing cloud configuration are now visible.

## Verification — 2026-11-17

An isolated instance of the real cloud-py app ran on localhost:8085 with its own temporary storage, a temporary test
app key and scripted demo agents. Unity's laptop hub address deliberately pointed to an unavailable port. Runtime
confirmed ForgeNet=Mock and ForgeCloud=Online, so the test did not fall back to the laptop workflow REST routes.

- Plan created through the Unity form and approved through its enabled button.
- Build b84a7e7d84974609995a0bb196d7938c reached ready. Builder, Tester, Scribe and Reviewer all completed.
- All four crew members appeared through polled cloud events, and the office showed ready.
- Independent verification ran six real Python tests; all passed. Cloud diff review loaded tipcalc.py and the other files.
- Targeted backend workflow regression: 26 passed. Unity compiled without errors.
- Original Unity connection settings were restored and the temporary app key removed after testing.

This verifies the cloud transport and scripted workflow, not live Claude reasoning, voice recognition, physical
controller input, or production authentication. The new source has not yet been installed on the Quest. The earlier
native Mac build predates this repair.

## Complete the device test

1. Connect and unlock the Quest; accept USB debugging. adb devices -l must show an authorized device.
2. Inspect the installed package/version and its connection errors before replacing it. Preserve application data.
3. Configure the real cloud app key locally and confirm that the deployed service exposes the build tools.
4. Build and install an updated APK containing this repair. Check headset logs for cloud authentication and polling.
5. From Home → Tools → Agents → Project workflow, create and approve a bounded task. Verify crew spawn/progress,
   diff and checks. Reopen the panel and repeat on the intended Wi-Fi connection without the laptop hub.
6. Separately verify valid provider credentials and reviewed GitHub publication. Do not treat scripted output as that test.
