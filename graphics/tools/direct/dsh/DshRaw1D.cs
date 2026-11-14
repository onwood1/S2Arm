// Ambiguous 1D fetches keep their real resource type until argument binding.
// The register payload of an integer fetch is bit-preserved, including 8/16-bit
// extension performed by Metal. No game key or descriptor format is baked in.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static S2A.Dsh.Py;

namespace S2A.Dsh
{
    internal static class DshRaw1D
    {
        [ThreadStatic] internal static bool Suppress;
        internal sealed record Binding(string Name, int FirstId, long Reg, long Off);
        internal sealed record Plan(string Prefix, List<Binding> Bindings);

        internal static Plan Build(string key, List<object> bindings, JsonObject res)
        {
            if (Suppress) return new Plan("s2a_raw1d_" + key, new List<Binding>());
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            if (res["fetch1d_ubo"] is JsonArray fetches)
                foreach (var f in fetches) wanted.Add((string)f);
            var selected = new List<Binding>();
            int next = 0;
            foreach (var b in bindings.OfType<PyDict>())
            {
                string ty = (string)b["msl_type"];
                int n = 1;
                var array = Regex.Match(ty ?? "", @"^array<.+,\s*(\d+)>$");
                if (array.Success) n = int.Parse(array.Groups[1].Value);
                next = Math.Max(next, Convert.ToInt32(b["arg_id"]) + n);
            }
            foreach (var b in bindings.OfType<PyDict>().ToArray())
            {
                if ((string)b["kind"] != "tex_ubo" || (string)b["msl_type"] != "texture2d<float>") continue;
                string nm = (string)b["msl_name"];
                int pos = nm.LastIndexOf("_cb", StringComparison.Ordinal);
                if (pos < 0 || !wanted.Contains(nm[(pos + 1)..])) continue;
                if (next + 4 > 1024) throw new DshError("raw 1D fetch", "argument buffer exceeds 1024 entries");
                selected.Add(new Binding(nm, next, Convert.ToInt64(b["reg"]), Convert.ToInt64(b["off"])));
                foreach (string type in new[] { "float", "uint", "int" })
                    bindings.Add(new PyDict { ["msl_name"] = nm + "_raw_" + type, ["msl_type"] = "texture_buffer<" + type + ">",
                        ["arg_id"] = (long)next++, ["kind"] = "raw1d_tex", ["reg"] = b["reg"], ["off"] = b["off"] });
                bindings.Add(new PyDict { ["msl_name"] = nm + "_raw_kind", ["msl_type"] = "constant uint*",
                    ["arg_id"] = (long)next++, ["kind"] = "raw1d_info", ["reg"] = b["reg"], ["off"] = b["off"] });
            }
            return new Plan("s2a_raw1d_" + key, selected);
        }

        internal static string Rewrite(string source, Plan plan)
        {
            if (source == null || plan.Bindings.Count == 0) return source;
            var declarations = new StringBuilder();
            foreach (var b in plan.Bindings)
            {
                int id = b.FirstId;
                foreach (string type in new[] { "float", "uint", "int" })
                    declarations.Append($"    texture_buffer<{type}> {b.Name}_raw_{type} [[id({id++})]];\n");
                declarations.Append($"    constant uint* {b.Name}_raw_kind [[id({id})]];\n");
                string t = "spvDescriptorSet0." + b.Name;
                string args = $"{t}, {t}_raw_float, {t}_raw_uint, {t}_raw_int, *{t}_raw_kind";
                foreach (string dimension in new[] { "width", "height" })
                {
                    string pattern = Regex.Escape(t) + "\\.get_" + dimension + @"\(\s*\)";
                    source = Regex.Replace(source, pattern, _ => $"{plan.Prefix}_{dimension}({args})");
                    source = source.Replace(t + ".get_" + dimension + "(", plan.Prefix + "_" + dimension + "(" + args + ", ", StringComparison.Ordinal);
                }
                source = source.Replace(t + ".read(", plan.Prefix + "_read(" + args + ", ", StringComparison.Ordinal);
            }
            var structure = Regex.Match(source, @"struct spvDescriptorSetBuffer0\s*\{[\s\S]*?\r?\n\};");
            if (!structure.Success) throw new DshError("raw 1D fetch", "argument buffer struct missing");
            source = source.Insert(structure.Index + structure.Length - 2, declarations.ToString());
            string prefix = plan.Prefix;
            string helper = $$"""
inline uint {{prefix}}_width(texture2d<float> t, texture_buffer<float> f, texture_buffer<uint> u, texture_buffer<int> i, uint kind, uint lod = 0) {
    return kind == 1 ? f.get_width() : kind == 2 ? u.get_width() : kind == 3 ? i.get_width() : t.get_width(lod);
}
inline uint {{prefix}}_height(texture2d<float> t, texture_buffer<float> f, texture_buffer<uint> u, texture_buffer<int> i, uint kind, uint lod = 0) {
    return kind ? 1u : t.get_height(lod);
}
inline float4 {{prefix}}_read(texture2d<float> t, texture_buffer<float> f, texture_buffer<uint> u, texture_buffer<int> i, uint kind, uint2 coord, uint lod = 0) {
    if (kind == 1) return f.read(coord.x);
    if (kind == 2) return as_type<float4>(u.read(coord.x));
    if (kind == 3) return as_type<float4>(i.read(coord.x));
    return t.read(coord, lod);
}

""";
            int at = source.IndexOf("struct spvDescriptorSetBuffer0", StringComparison.Ordinal);
            return source.Insert(at, helper);
        }
    }
}
