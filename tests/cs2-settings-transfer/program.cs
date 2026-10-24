using NfaLoader.Services;

var root = Path.Combine(Path.GetTempPath(), "cs2-settings-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var checks = 0;
const string source = "76561197960265729";
const string target = "76561197960265730";
const string second = "76561197960265731";
var userdata = Path.Combine(root, "userdata");
var backups = Path.Combine(root, "backups");
var running = false;
var guardCalls = 0;
var failAt = int.MaxValue;
var service = new Cs2SettingsTransferService(() => running || ++guardCalls == failAt, backups);
string FilePath(string id, string relative) => Path.Combine(userdata, (ulong.Parse(id) - 76561197960265728UL).ToString(), relative.Replace('/', Path.DirectorySeparatorChar));
void Put(string id, string relative, string text)
{
    var path = FilePath(id, relative);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, text);
}
void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    checks++;
}
void Throws(Action action, string label)
{
    try { action(); } catch { checks++; return; }
    throw new Exception(label);
}
const string convars = "730/local/cfg/cs2_user_convars_0_slot0.vcfg";
const string keys = "730/local/cfg/cs2_user_keys_0_slot0.vcfg";
try
{
    Put(source, convars, "\"config\" { \"convars\" { \"sensitivity\" \"2\" \"cl_crosshairsize\" \"7\" \"volume\" \"0.6\" \"unknown_account_state\" \"private\" } }");
    Put(source, keys, "\"config\" { \"bindings\" { \"w\" \"+forward\" } \"unknown\" \"private\" }");
    Put(source, "730/local/cfg/cs2_video.txt", "\"video\" { \"setting.defaultres\" \"1920\" }");
    Put(source, "730/local/cfg/autoexec.cfg", "echo hello");
    Put(source, "730/remote/socache.dt", "private");
    Put(target, convars, "\"config\" { \"convars\" { \"sensitivity\" \"1\" \"cl_crosshairsize\" \"3\" \"volume\" \"0.1\" \"target_only\" \"keep\" } }");
    var original = File.ReadAllBytes(FilePath(target, convars));
    var sourceOriginal = File.ReadAllBytes(FilePath(source, convars));
    var snapshot = service.Capture(userdata, source, Cs2SettingsCategory.Crosshair);
    Check(snapshot.FileCount == 2 && snapshot.MissingCategories == Cs2SettingsCategory.None, "crosshair capture");
    Check(service.Preview(snapshot, target).Files.Count == 2 && service.Preview(snapshot, target).Files.All(f => f.Changed), "preview");
    Check(File.ReadAllBytes(FilePath(target, convars)).SequenceEqual(original), "preview is read-only");
    var applied = service.Apply(snapshot, [target]).Single();
    Check(applied.Ok && applied.FilesWritten == 2 && applied.BackupPath != null, "apply crosshair");
    var document = VdfDocument.GetObject(VdfDocument.GetObject(VdfDocument.Load(FilePath(target, convars)), "config")!, "convars")!;
    Check(VdfDocument.GetString(document, "sensitivity") == "1", "controls isolated");
    Check(VdfDocument.GetString(document, "volume") == "0.1", "audio isolated");
    Check(VdfDocument.GetString(document, "cl_crosshairsize") == "7", "crosshair merged");
    Check(VdfDocument.GetString(document, "target_only") == "keep" && !document.ContainsKey("unknown_account_state"), "unknown target preserved source excluded");
    Check(File.ReadAllBytes(FilePath(source, convars)).SequenceEqual(sourceOriginal), "source unchanged");
    Check(service.ListBackups().Count == 1, "backup listed");
    var restored = service.Restore(applied.BackupPath!);
    Check(restored.Ok && restored.BackupPath != applied.BackupPath, "restore backs up current target");
    Check(File.ReadAllBytes(FilePath(target, convars)).SequenceEqual(original), "original restored exactly");
    Check(!service.Restore(applied.BackupPath!, Path.Combine(root, "other-userdata")).Ok, "backup bound to expected userdata root");
    Check(service.Apply(snapshot, [source]).Single().Ok == false, "same account rejected");
    Check(!service.Apply(snapshot, ["0" + source]).Single().Ok, "same account alias rejected");
    var all = service.Capture(userdata, source, Cs2SettingsCategory.All);
    Check(all.FileCount == 6, "all eligible files captured");
    var fresh = service.Apply(all, [second]).Single();
    Check(fresh.Ok && fresh.FilesWritten == 6 && !File.Exists(FilePath(second, "730/remote/socache.dt")), "absent target created state excluded");
    Check(service.Restore(fresh.BackupPath!).Ok && !File.Exists(FilePath(second, convars)) && !File.Exists(FilePath(second, "730/local/cfg/autoexec.cfg")), "restore removes originally absent files");
    var bulk = service.Apply(snapshot, ["bad", target]);
    Check(!bulk[0].Ok && bulk[1].Ok, "bulk partial failure");
    var last = bulk[1];
    var originalBin = Path.Combine(Path.GetDirectoryName(last.BackupPath!)!, "0000.bin");
    File.AppendAllText(originalBin, "tamper");
    Check(!service.Restore(last.BackupPath!).Ok, "hash tamper rejected");
    running = true;
    Check(!service.Apply(snapshot, [target]).Single().Ok, "running process blocks apply");
    Check(!service.Restore(applied.BackupPath!).Ok, "running process blocks restore");
    Throws(() => service.Capture(userdata, source, Cs2SettingsCategory.All), "running process blocks capture");
    running = false;
    Put(source, convars, "\"config\" { \"bad\"");
    Throws(() => service.Capture(userdata, source, Cs2SettingsCategory.Crosshair), "malformed source rejected");
    File.WriteAllBytes(FilePath(source, convars), sourceOriginal);
    var beforeFailure = File.ReadAllBytes(FilePath(target, convars));
    guardCalls = 0;
    failAt = 3;
    var interrupted = service.Apply(all, [target]).Single();
    Check(!interrupted.Ok && File.ReadAllBytes(FilePath(target, convars)).SequenceEqual(beforeFailure), "mid-transaction failure rolls back");
    failAt = int.MaxValue;
    var missing = service.Capture(userdata, second, Cs2SettingsCategory.All);
    Check(missing.FileCount == 0 && missing.MissingCategories == Cs2SettingsCategory.All, "missing categories reported");
    var controls = service.Capture(userdata, source, Cs2SettingsCategory.Controls);
    Check(controls.FileCount == 4, "controls includes binds and input");
    var controlTarget = "76561197960265733";
    Check(service.Apply(controls, [controlTarget]).Single().Ok, "controls apply");
    var controlDocument = VdfDocument.GetObject(VdfDocument.GetObject(VdfDocument.Load(FilePath(controlTarget, convars)), "config")!, "convars")!;
    Check(controlDocument.Count == 1 && VdfDocument.GetString(controlDocument, "sensitivity") == "2", "controls excludes crosshair audio state");
    Check(VdfDocument.GetObject(VdfDocument.GetObject(VdfDocument.Load(FilePath(controlTarget, keys)), "config")!, "bindings")!.Count == 1, "binds copied");
    Put(source, convars, "\"config\" { \"convars\" { \"cl_crosshairsize\" \"99\" } }");
    Check(service.Apply(snapshot, [second]).Single().Ok, "snapshot remains usable");
    Check(VdfDocument.GetString(VdfDocument.GetObject(VdfDocument.GetObject(VdfDocument.Load(FilePath(second, convars)), "config")!, "convars")!, "cl_crosshairsize") == "7", "snapshot detached from source");
    using (var gate = new FileStream(Path.Combine(backups, "transfer.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        Check(!service.Apply(snapshot, [target]).Single().Ok, "exclusive lock enforced");
    var unsafeManifest = service.Apply(controls, ["76561197960265734"]).Single().BackupPath!;
    var manifestText = File.ReadAllText(unsafeManifest);
    File.WriteAllText(unsafeManifest, manifestText.Replace("730/local/cfg/", "730/../cfg/", StringComparison.Ordinal));
    Check(!service.Restore(unsafeManifest).Ok, "malicious manifest path rejected");
    var linkedAccount = Path.Combine(userdata, "4");
    var linkInfo = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    linkInfo.ArgumentList.Add("/c");
    linkInfo.ArgumentList.Add("mklink");
    linkInfo.ArgumentList.Add("/J");
    linkInfo.ArgumentList.Add(linkedAccount);
    linkInfo.ArgumentList.Add(Path.Combine(userdata, "2"));
    using (var link = System.Diagnostics.Process.Start(linkInfo)!)
    {
        link.WaitForExit();
        Check(link.ExitCode == 0, "test junction created");
    }
    Check(!service.Apply(snapshot, ["76561197960265732"]).Single().Ok, "linked account rejected");
    Directory.Delete(linkedAccount);
    const string schemaSource = "76561197960265738";
    const string schemaTarget = "76561197960265739";
    const string machine = "730/local/cfg/cs2_machine_convars.vcfg";
    Dictionary<string, object> Convars(string id, string path) => VdfDocument.GetObject(VdfDocument.GetObject(VdfDocument.Load(FilePath(id, path)), "config")!, "convars")!;
    Put(schemaSource, machine, "\"config\" { \"convars\" { \"volume\" \"0.8\" \"voice_threshold$2\" \"0.2\" \"cl_hud_telemetry_frametime_show\" \"2\" \"hud_scaling\" \"0.9\" \"speaker_config\" \"2\" \"closecaption\" \"1\" \"unknown_machine_state\" \"private\" } }");
    Put(schemaTarget, machine, "\"config\" { \"convars\" { \"volume\" \"0.1\" \"voice_threshold$1\" \"0.9\" \"unknown_target\" \"keep\" } }");
    var audioSnapshot = service.Capture(userdata, schemaSource, Cs2SettingsCategory.HudAudio);
    Check(audioSnapshot.FileCount == 1, "machine convars captured");
    Check(service.Apply(audioSnapshot, [schemaTarget]).Single().Ok, "machine convars applied");
    var audio = Convars(schemaTarget, machine);
    Check(VdfDocument.GetString(audio, "volume") == "0.8" && VdfDocument.GetString(audio, "hud_scaling") == "0.9", "machine volume and hud copied");
    Check(audio.ContainsKey("voice_threshold$2") && !audio.ContainsKey("voice_threshold$1"), "numeric suffix matched original source key retained");
    Check(VdfDocument.GetString(audio, "unknown_target") == "keep" && !audio.ContainsKey("unknown_machine_state"), "machine unknown fields isolated");
    Put(schemaSource, convars, "\"config\" { \"convars\" { \"cl_crosshairsize\" \"11\" \"cl_grenadecrosshair_decoy\" \"1\" \"cl_ironsight_dot_scale\" \"2\" \"sensitivity_y_scale\" \"1.2\" \"input_button_code_is_scan_code_scd\" \"1\" \"joy_axisr_deadzone\" \"0.1\" \"cl_joystick_enabled\" \"0\" } }");
    Put(schemaSource, "730/remote/cs2_user_convars.vcfg", "\"config\" { \"convars\" { \"cl_crosshairsize\" \"1\" \"sensitivity_y_scale\" \"0.1\" } }");
    var canonical = service.Capture(userdata, schemaSource, Cs2SettingsCategory.Crosshair | Cs2SettingsCategory.Controls);
    const string remote = "730/remote/cs2_user_convars.vcfg";
    Check(service.Apply(canonical, [schemaTarget]).Single().Ok, "canonical settings applied");
    Check(VdfDocument.GetString(Convars(schemaTarget, convars), "cl_crosshairsize") == "11" &&
        VdfDocument.GetString(Convars(schemaTarget, remote), "cl_crosshairsize") == "11", "fresh local settings mirrored over stale remote");
    Check(VdfDocument.GetString(Convars(schemaTarget, convars), "sensitivity_y_scale") == "1.2" &&
        VdfDocument.GetString(Convars(schemaTarget, convars), "joy_axisr_deadzone") == "0.1", "actual input keys classified");
    Check(VdfDocument.GetString(Convars(schemaTarget, convars), "cl_grenadecrosshair_decoy") == "1" &&
        VdfDocument.GetString(Convars(schemaTarget, convars), "cl_ironsight_dot_scale") == "2", "actual crosshair keys classified");
    const string remoteOnlySource = "76561197960265740";
    const string remoteOnlyTarget = "76561197960265741";
    Put(remoteOnlySource, remote, "\"config\" { \"convars\" { \"cl_crosshairsize\" \"13\" } }");
    Put(remoteOnlyTarget, remote, "\"config\" { \"convars\" { \"cl_crosshairsize\" \"3\" \"sensitivity\" \"2.75\" \"volume\" \"0.33\" \"target_only\" \"keep\" } }");
    Put(remoteOnlyTarget, "730/remote/cfg/cs2_user_convars.vcfg", "\"config\" { \"convars\" { \"cl_crosshairsize\" \"3\" \"mirror_only\" \"keep\" } }");
    var legacy = service.Capture(userdata, remoteOnlySource, Cs2SettingsCategory.Crosshair);
    Check(service.Apply(legacy, [remoteOnlyTarget]).Single().Ok, "remote-only account paste");
    Check(VdfDocument.GetString(Convars(remoteOnlyTarget, convars), "cl_crosshairsize") == "13" &&
        VdfDocument.GetString(Convars(remoteOnlyTarget, remote), "target_only") == "keep", "remote-only source hydrates local preserves remote fields");
    Check(VdfDocument.GetString(Convars(remoteOnlyTarget, convars), "sensitivity") == "2.75" &&
        VdfDocument.GetString(Convars(remoteOnlyTarget, convars), "volume") == "0.33", "new local mirror preserves remote target unselected values");
    Check(VdfDocument.GetString(Convars(remoteOnlyTarget, "730/remote/cfg/cs2_user_convars.vcfg"), "cl_crosshairsize") == "13" &&
        VdfDocument.GetString(Convars(remoteOnlyTarget, "730/remote/cfg/cs2_user_convars.vcfg"), "mirror_only") == "keep", "existing remote cfg mirror updated");
    const string slot = "730/local/cfg/cs2_user_convars_1_slot1.vcfg";
    Put(remoteOnlySource, slot, "\"config\" { \"convars\" { \"cl_crosshairsize\" \"17\" } }");
    Check(service.Apply(service.Capture(userdata, remoteOnlySource, Cs2SettingsCategory.Crosshair), [remoteOnlyTarget]).Single().Ok, "multiple slots pasted");
    Check(VdfDocument.GetString(Convars(remoteOnlyTarget, slot), "cl_crosshairsize") == "17" &&
        VdfDocument.GetString(Convars(remoteOnlyTarget, "730/remote/cs2_user_convars_1_slot1.vcfg"), "cl_crosshairsize") == "17" &&
        VdfDocument.GetString(Convars(remoteOnlyTarget, convars), "cl_crosshairsize") == "13", "slot identities preserved");
    Put(schemaSource, "730/local/cfg/trustedlaunch.cfg", "security-state");
    Put(schemaSource, "730/local/cfg/custom.cfg", "echo custom");
    var custom = service.Capture(userdata, schemaSource, Cs2SettingsCategory.CustomConfigs);
    Check(custom.FileCount == 1 && custom.Payloads[0].RelativePath.EndsWith("custom.cfg"), "trustedlaunch excluded");
    Put(schemaSource, convars, "malformed {");
    Put(schemaSource, machine, "malformed {");
    Put(schemaSource, "730/local/cfg/cs2_video.txt", "\"video\" { \"setting.defaultres\" \"1280\" }");
    Check(service.Capture(userdata, schemaSource, Cs2SettingsCategory.Video).FileCount == 1, "video ignores malformed unrelated convars");
    var emptySnapshot = service.Capture(userdata, "76561197960265799", Cs2SettingsCategory.All);
    Check(!service.Apply(emptySnapshot, [target]).Single().Ok, "empty snapshot cannot report success");
    const string seedSource = "76561197960265750";
    const string seedTarget = "76561197960265751";
    Put(seedSource, convars, "\"config\" { \"convars\" { \"cl_crosshairsize\" \"21\" \"sensitivity\" \"99\" \"volume\" \"0.99\" } }");
    Put(seedTarget, convars, "\"config\" { \"convars\" { \"cl_crosshairsize\" \"4\" \"sensitivity\" \"1.75\" \"volume\" \"0.22\" } \"metadata\" \"keep-local\" }");
    var seedSnapshot = service.Capture(userdata, seedSource, Cs2SettingsCategory.Crosshair);
    var seedResult = service.Apply(seedSnapshot, [seedTarget]).Single();
    Check(seedResult.Ok, "local-only target mirrored");
    var seededRemote = Convars(seedTarget, remote);
    Check(VdfDocument.GetString(seededRemote, "sensitivity") == "1.75" && VdfDocument.GetString(seededRemote, "volume") == "0.22" &&
        VdfDocument.GetString(seededRemote, "cl_crosshairsize") == "21", "new remote mirror preserves local target unselected values excludes source input");
    Check(VdfDocument.GetString(VdfDocument.GetObject(VdfDocument.Load(FilePath(seedTarget, remote)), "config")!, "metadata") == "keep-local", "new mirror preserves target metadata");
    Check(service.Restore(seedResult.BackupPath!).Ok && !File.Exists(FilePath(seedTarget, remote)), "seeded absent mirror restore removes new file");
    Put(seedSource, keys, "\"config\" { \"bindings\" { \"w\" \"+forward\" } }");
    Put(seedTarget, "730/remote/cs2_user_keys.vcfg", "\"config\" { \"bindings\" { \"f\" \"+lookatweapon\" } \"metadata\" \"keep-keys\" }");
    Check(service.Apply(service.Capture(userdata, seedSource, Cs2SettingsCategory.Controls), [seedTarget]).Single().Ok, "keys mirror seeded");
    var seededKeys = VdfDocument.GetObject(VdfDocument.Load(FilePath(seedTarget, keys)), "config")!;
    Check(VdfDocument.GetString(VdfDocument.GetObject(seededKeys, "bindings")!, "f") == "+lookatweapon" &&
        VdfDocument.GetString(seededKeys, "metadata") == "keep-keys", "new keys mirror preserves target binds and metadata");
    var validBackupCount = service.ListBackups().Count;
    foreach (var bad in new[] { "null", "[null]", "[{\"RelativePath\":null}]" })
    {
        var folder = Path.Combine(backups, "corrupt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var badPath = Path.Combine(folder, "manifest.json");
        File.WriteAllText(badPath, "{\"Version\":1,\"UserdataRoot\":\"fake\",\"SourceSteamId64\":\"" + source + "\",\"TargetSteamId64\":\"" + target + "\",\"Files\":" + bad + "}");
        Check(!service.Restore(badPath).Ok, "corrupt backup restore rejected");
    }
    Check(service.ListBackups().Count == validBackupCount && validBackupCount > 0, "corrupt manifests skipped valid backups remain listed");
    Console.WriteLine($"Passed {checks} checks.");
}
finally
{
    Directory.Delete(root, true);
}
