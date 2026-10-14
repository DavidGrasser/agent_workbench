using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ClaudeWorkflowSetup
{
    public enum FieldKind { Boolean, Choice, Text, Integer, Number, List, Env, Json }

    public sealed class SettingField
    {
        public string Key;
        public FieldKind Kind;
        public List<string> Choices = new List<string>();
        public string Description = "";
        public bool Hidden;
    }

    // One coding tool's settings file and the published schema that describes it.
    public sealed class ToolSettings
    {
        public string Key;
        public string Name;
        public string Schema;
        public bool Toml;
        public string RelativePath;
        public string[] Expand = new string[0];
        public string[] Common = new string[0];

        public static readonly ToolSettings[] All =
        {
            new ToolSettings
            {
                Key = "claude", Name = "Claude Code", Schema = "settings-schema.json", RelativePath = "/.claude/settings.json", Expand = new[] { "permissions" },
                Common = new[] { "model", "effortLevel", "language", "theme", "tui", "editorMode", "viewMode", "outputStyle", "fastMode",
                    "alwaysThinkingEnabled", "autoCompactEnabled", "autoMemoryEnabled", "teammateMode", "voice",
                    "preferredNotifChannel", "cleanupPeriodDays", "attribution", "includeGitInstructions", "verbose" },
            },
            new ToolSettings
            {
                Key = "codex", Name = "Codex", Schema = "codex-schema.json", Toml = true, RelativePath = "/.codex/config.toml",
                Common = new[] { "model", "model_provider", "model_reasoning_effort", "model_verbosity", "approval_policy", "sandbox_mode",
                    "profile", "hide_agent_reasoning", "check_for_update_on_startup", "notify" },
            },
            new ToolSettings
            {
                Key = "opencode", Name = "OpenCode", Schema = "opencode-schema.json", RelativePath = "/.config/opencode/opencode.json",
                Common = new[] { "model", "small_model", "default_agent", "username", "share", "autoupdate", "snapshot", "instructions", "permission" },
            },
        };
    }

    // Settings fields generated from a tool's JSON schema; $ref and single allOf wrappers are resolved.
    public static class SettingsSchema
    {
        static readonly Dictionary<string, List<SettingField>> cache = new Dictionary<string, List<SettingField>>();

        public static List<SettingField> For(ToolSettings tool)
        {
            if (cache.TryGetValue(tool.Key, out var known)) return known;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(tool.Schema))
            using (var reader = new StreamReader(stream))
            {
                var root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
                var defs = (root.TryGetValue("$defs", out var d) ? d : root.TryGetValue("definitions", out var d2) ? d2 : null) as Dictionary<string, object>
                           ?? new Dictionary<string, object>();
                var node = Resolve(root, defs);
                var result = new List<SettingField>();
                foreach (var kv in (Dictionary<string, object>)node["properties"])
                {
                    if (kv.Key == "$schema") continue;
                    var p = (Dictionary<string, object>)kv.Value;
                    var resolved = Resolve(p, defs);
                    if (tool.Expand.Contains(kv.Key) && resolved.TryGetValue("properties", out var sub))
                    {
                        foreach (var s in (Dictionary<string, object>)sub)
                            result.Add(Describe(kv.Key + "." + s.Key, (Dictionary<string, object>)s.Value, defs));
                        continue;
                    }
                    result.Add(Describe(kv.Key, p, defs));
                }
                return cache[tool.Key] = result;
            }
        }

        static Dictionary<string, object> Resolve(Dictionary<string, object> node, Dictionary<string, object> defs)
        {
            for (int guard = 0; guard < 10; guard++)
            {
                if (node.TryGetValue("$ref", out var r) && r is string reference)
                {
                    var target = defs.TryGetValue(reference.Split('/').Last(), out var t) ? t as Dictionary<string, object> : null;
                    if (target == null) return node;
                    node = Merge(target, node);
                }
                else if (node.TryGetValue("allOf", out var a) && a is ArrayList all && all.Count == 1 && all[0] is Dictionary<string, object> only)
                    node = Merge(only, node);
                else return node;
            }
            return node;
        }

        // The outer node's description wins over the referenced definition's.
        static Dictionary<string, object> Merge(Dictionary<string, object> inner, Dictionary<string, object> outer)
        {
            var merged = new Dictionary<string, object>(inner);
            if (outer.TryGetValue("description", out var description)) merged["description"] = description;
            return merged;
        }

        static SettingField Describe(string key, Dictionary<string, object> raw, Dictionary<string, object> defs)
        {
            var p = Resolve(raw, defs);
            var field = new SettingField { Key = key, Description = (p.TryGetValue("description", out var d) ? d as string : "") ?? "" };
            var text = field.Description.TrimStart();
            field.Hidden = text.Contains("(Managed settings only)") || text.StartsWith("(Admin/managed settings only)") || text.StartsWith("Experimental / do not use")
                           || text.Contains("@deprecated") || text.StartsWith("Deprecated") || text.StartsWith("DEPRECATED") || text.StartsWith("Compatibility-only");
            object type = p.TryGetValue("type", out var t) ? t : null;
            if (type is ArrayList types) type = types.Cast<object>().FirstOrDefault(x => (string)x != "null");

            var alternatives = (p.TryGetValue("anyOf", out var any) ? any : p.TryGetValue("oneOf", out var one) ? one : null) as ArrayList;
            var options = alternatives?.OfType<Dictionary<string, object>>().Select(x => Resolve(x, defs)).ToList();

            if (key == "env") field.Kind = FieldKind.Env;
            else if (p.TryGetValue("enum", out var e) && e is ArrayList values) { field.Kind = FieldKind.Choice; field.Choices = values.Cast<object>().Select(x => x?.ToString()).Where(x => x != null).ToList(); }
            else if ((string)type == "boolean") field.Kind = FieldKind.Boolean;
            else if ((string)type == "integer") field.Kind = FieldKind.Integer;
            else if ((string)type == "number") field.Kind = FieldKind.Number;
            else if ((string)type == "string") field.Kind = FieldKind.Text;
            else if ((string)type == "array" && p.TryGetValue("items", out var i) && i is Dictionary<string, object> items
                     && Resolve(items, defs).TryGetValue("type", out var it) && (it as string) == "string") field.Kind = FieldKind.List;
            else if (options != null)
            {
                var choices = options.SelectMany(o => o.TryGetValue("enum", out var oe) && oe is ArrayList ol ? ol.Cast<object>()
                    : o.TryGetValue("const", out var c) ? new[] { c } : Enumerable.Empty<object>()).Select(x => x?.ToString()).Where(x => x != null).Distinct().ToList();
                bool allStrings = options.All(o => (o.TryGetValue("type", out var ot) ? ot as string : null) == "string" || o.ContainsKey("const"));
                bool allBooleans = options.All(o => (o.TryGetValue("type", out var ot) ? ot as string : null) == "boolean");
                if (allBooleans) field.Kind = FieldKind.Boolean;
                else if (choices.Count > 0) { field.Kind = FieldKind.Choice; field.Choices = choices; }
                else field.Kind = allStrings ? FieldKind.Text : FieldKind.Json;
            }
            else field.Kind = FieldKind.Json;
            return field;
        }
    }

    // One settings file: JSON for Claude Code and OpenCode, TOML for Codex.
    public sealed class SettingsFile
    {
        public string Distro;
        public string LinuxPath;
        public bool Toml;
        public Dictionary<string, object> Values = new Dictionary<string, object>();

        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public static SettingsFile Read(string distro, string linuxPath, bool toml)
        {
            var file = new SettingsFile { Distro = distro, LinuxPath = linuxPath, Toml = toml };
            if (toml)
            {
                var (code, output) = Run(distro, @"
import json, os, sys, tomllib
p = sys.argv[1]
print(json.dumps(tomllib.load(open(p, 'rb')) if os.path.exists(p) else {}, default=str))
", linuxPath);
                if (code != 0) throw new InvalidOperationException("config.toml could not be read: " + output);
                file.Values = Json.Deserialize<Dictionary<string, object>>(output.Trim());
                return file;
            }
            var windows = Projects.WindowsPath(distro, linuxPath);
            if (File.Exists(windows))
            {
                var text = File.ReadAllText(windows).Trim();
                if (text != "") file.Values = Json.Deserialize<Dictionary<string, object>>(text);
            }
            return file;
        }

        public object Get(string key)
        {
            int dot = key.IndexOf('.');
            if (dot < 0) return Values.TryGetValue(key, out var v) ? v : null;
            return Values.TryGetValue(key.Substring(0, dot), out var p) && p is Dictionary<string, object> parent
                   && parent.TryGetValue(key.Substring(dot + 1), out var s) ? s : null;
        }

        public static string ToJson(object value) => Json.Serialize(value);

        public static object FromJson(string text) => Json.DeserializeObject(text);

        // Applies only the changed keys through python in WSL, after a backup. JSON keeps order and unknown
        // keys; TOML is edited line by line so comments survive, and is validated after writing.
        public Task<(int Code, string Output)> Save(Dictionary<string, object> set, List<string> remove, bool hideFromGit)
        {
            var change = Json.Serialize(new Dictionary<string, object> { ["set"] = set, ["remove"] = remove, ["toml"] = Toml, ["hide"] = hideFromGit });
            Directory.CreateDirectory(Payload.WorkDir);
            var changeFile = Path.Combine(Payload.WorkDir, $"settings-{Guid.NewGuid():N}.json");
            File.WriteAllText(changeFile, change, Shell.Utf8);
            return Task.Run(() =>
            {
                try { return Run(Distro, SaveScript, LinuxPath, Wsl.ToLinux(Distro, changeFile)); }
                finally { File.Delete(changeFile); }
            });
        }

        const string SaveScript = @"
import json, os, re, shutil, subprocess, sys, time
path, change = sys.argv[1], json.load(open(sys.argv[2]))
backup = None
if os.path.exists(path):
    backup = path + '.bak-' + time.strftime('%Y%m%d-%H%M%S')
    shutil.copy2(path, backup)
os.makedirs(os.path.dirname(path), exist_ok=True)

def split(key):
    return key.split('.', 1) if '.' in key else (key, None)

if not change['toml']:
    data = json.load(open(path)) if os.path.exists(path) and os.path.getsize(path) > 0 else {}
    for key, value in change['set'].items():
        top, sub = split(key)
        if sub: data.setdefault(top, {})[sub] = value
        else: data[top] = value
    for key in change['remove']:
        top, sub = split(key)
        if sub:
            data.get(top, {}).pop(sub, None)
            if top in data and not data[top]: del data[top]
        else: data.pop(top, None)
    with open(path, 'w') as f:
        json.dump(data, f, indent=2, ensure_ascii=False)
        f.write('\n')
else:
    import tomllib
    def tv(v):
        if isinstance(v, bool): return 'true' if v else 'false'
        if isinstance(v, (int, float)): return repr(v)
        if isinstance(v, str): return json.dumps(v, ensure_ascii=False)
        if isinstance(v, list): return '[' + ', '.join(tv(x) for x in v) + ']'
        if isinstance(v, dict): return '{ ' + ', '.join(tk(k) + ' = ' + tv(x) for k, x in v.items()) + ' }'
        return json.dumps(str(v))
    def tk(k): return k if re.fullmatch(r'[A-Za-z0-9_-]+', k) else json.dumps(k)
    lines = open(path).read().splitlines() if os.path.exists(path) else []
    def drop(key):
        global lines
        out, skipping, in_top = [], False, True
        for line in lines:
            s = line.strip()
            if s.startswith('['):
                in_top = False
                name = s.strip('[]').strip()
                skipping = name == key or name.startswith(key + '.') or name == tk(key) or name.startswith(tk(key) + '.')
            elif in_top and re.match(r'^\s*' + re.escape(tk(key)) + r'\s*=', line):
                continue
            if not skipping: out.append(line)
        lines = out
    for key in list(change['set']) + change['remove']:
        drop(split(key)[0] if split(key)[1] else key)
    first_table = next((i for i, l in enumerate(lines) if l.strip().startswith('[')), len(lines))
    current = tomllib.loads('\n'.join(lines)) if lines else {}
    new_top = []
    nested = {}
    for key, value in change['set'].items():
        top, sub = split(key)
        if sub: nested.setdefault(top, {})[sub] = value
        else: new_top.append(tk(top) + ' = ' + tv(value))
    for top, values in nested.items():
        merged = dict(current.get(top, {})) if isinstance(current.get(top), dict) else {}
        merged.update(values)
        new_top.append(tk(top) + ' = ' + tv(merged))
    top, rest = lines[:first_table], lines[first_table:]
    while top and top[-1].strip() == '': top.pop()
    lines = top + new_top + ([''] if rest else []) + rest
    open(path, 'w').write('\n'.join(lines).strip('\n') + '\n')
    try:
        tomllib.load(open(path, 'rb'))
    except Exception as e:
        if backup: shutil.copy2(backup, path)
        print('the TOML would be invalid, nothing was changed: ' + str(e)); sys.exit(1)

os.chmod(path, 0o600) if path.endswith(('.claude.json', 'config.toml')) else None
if change['hide']:
    root = os.path.dirname(os.path.dirname(path))
    common = subprocess.run(['git', '-C', root, 'rev-parse', '--git-common-dir'], capture_output=True, text=True).stdout.strip()
    if common:
        common = common if os.path.isabs(common) else os.path.join(root, common)
        exclude = os.path.join(common, 'info', 'exclude')
        os.makedirs(os.path.dirname(exclude), exist_ok=True)
        entries = open(exclude).read().splitlines() if os.path.exists(exclude) else []
        if '/.claude/settings.local.json' not in entries:
            open(exclude, 'a').write('/.claude/settings.local.json\n')
print('saved')
";

        static (int Code, string Output) Run(string distro, string python, params string[] args)
        {
            Directory.CreateDirectory(Payload.WorkDir);
            var script = Path.Combine(Payload.WorkDir, $"settings-{Guid.NewGuid():N}.py");
            Shell.WriteUnix(script, python);
            try
            {
                return Shell.Capture("wsl.exe", $"-d {distro} -e python3 \"{Wsl.ToLinux(distro, script)}\" " + string.Join(" ", args.Select(a => $"\"{a}\"")));
            }
            finally
            {
                File.Delete(script);
            }
        }
    }
}
