using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Serilog;

namespace RevitMCPBridge
{
    /// <summary>
    /// Scans the executing assembly for methods tagged with [MCPMethod] and
    /// builds a registry dictionary. Runs once at startup.
    ///
    /// Uses Delegate.CreateDelegate to produce direct Func references —
    /// per-request cost is a dictionary lookup, not reflection invoke.
    /// </summary>
    public static class MCPMethodScanner
    {
        private static readonly Type[] ExpectedParams =
            { typeof(UIApplication), typeof(JObject) };

        /// <summary>
        /// Metadata about a discovered MCP method.
        /// </summary>
        public class MethodInfo
        {
            public string Name { get; set; }
            public string Category { get; set; }
            public string Description { get; set; }
            public string DeclaringType { get; set; }
            public string MethodName { get; set; }
            public string[] Aliases { get; set; }
        }

        /// <summary>
        /// Result of a scan operation.
        /// </summary>
        public class ScanResult
        {
            public Dictionary<string, Func<UIApplication, JObject, string>> Methods { get; set; }
            public Dictionary<string, MethodInfo> Metadata { get; set; }
            public int MethodCount { get; set; }
            public int AliasCount { get; set; }
            public List<string> Conflicts { get; set; }
        }

        /// <summary>
        /// Scan all types in the executing assembly for [MCPMethod] attributes.
        /// Returns methods as direct Func delegates plus metadata for discovery APIs.
        /// </summary>
        public static ScanResult Scan()
        {
            var methods = new Dictionary<string, Func<UIApplication, JObject, string>>(
                StringComparer.OrdinalIgnoreCase);
            var metadata = new Dictionary<string, MethodInfo>(
                StringComparer.OrdinalIgnoreCase);
            var conflicts = new List<string>();
            int methodCount = 0;
            int aliasCount = 0;

            var assembly = Assembly.GetExecutingAssembly();

            foreach (var type in assembly.GetTypes())
            {
                // Only scan static classes (all *Methods.cs are static classes)
                if (!type.IsClass || !type.IsAbstract || !type.IsSealed)
                    continue;

                foreach (var method in type.GetMethods(
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    var attr = method.GetCustomAttribute<MCPMethodAttribute>();
                    if (attr == null)
                        continue;

                    // Validate signature: must be (UIApplication, JObject) → string
                    if (!ValidateSignature(method, type))
                        continue;

                    // Create a direct Func delegate — NO reflection at call time
                    Func<UIApplication, JObject, string> del;
                    try
                    {
                        del = (Func<UIApplication, JObject, string>)Delegate.CreateDelegate(
                            typeof(Func<UIApplication, JObject, string>), method);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[MCPMethodScanner] Failed to create delegate for {Type}.{Method}",
                            type.Name, method.Name);
                        continue;
                    }

                    // Build metadata
                    var info = new MethodInfo
                    {
                        Name = attr.Name,
                        Category = attr.Category ?? InferCategory(type.Name),
                        Description = attr.Description ?? $"{type.Name}.{method.Name}",
                        DeclaringType = type.Name,
                        MethodName = method.Name,
                        Aliases = attr.Aliases
                    };

                    // Register primary name
                    Register(methods, metadata, attr.Name, del, info, type, method, conflicts);
                    methodCount++;

                    // Register aliases
                    foreach (var alias in attr.Aliases)
                    {
                        if (!string.IsNullOrWhiteSpace(alias))
                        {
                            Register(methods, metadata, alias, del, info, type, method, conflicts);
                            aliasCount++;
                        }
                    }
                }
            }

            Log.Information(
                "[MCPMethodScanner] Discovered {Count} methods + {Aliases} aliases from {Types} types",
                methodCount, aliasCount, assembly.GetTypes().Count(t => t.IsClass && t.IsAbstract && t.IsSealed));

            if (conflicts.Count > 0)
            {
                Log.Warning("[MCPMethodScanner] {Count} name conflicts (last wins): {Conflicts}",
                    conflicts.Count, string.Join(", ", conflicts));
            }

            return new ScanResult
            {
                Methods = methods,
                Metadata = metadata,
                MethodCount = methodCount,
                AliasCount = aliasCount,
                Conflicts = conflicts
            };
        }

        private static bool ValidateSignature(System.Reflection.MethodInfo method, Type type)
        {
            if (method.ReturnType != typeof(string))
            {
                Log.Warning("[MCPMethodScanner] {Type}.{Method}: wrong return type {Ret} (expected string)",
                    type.Name, method.Name, method.ReturnType.Name);
                return false;
            }

            var parms = method.GetParameters();
            if (parms.Length != 2 ||
                parms[0].ParameterType != ExpectedParams[0] ||
                parms[1].ParameterType != ExpectedParams[1])
            {
                Log.Warning("[MCPMethodScanner] {Type}.{Method}: wrong signature (expected UIApplication, JObject)",
                    type.Name, method.Name);
                return false;
            }

            return true;
        }

        private static void Register(
            Dictionary<string, Func<UIApplication, JObject, string>> methods,
            Dictionary<string, MethodInfo> metadata,
            string name,
            Func<UIApplication, JObject, string> del,
            MethodInfo info,
            Type type,
            System.Reflection.MethodInfo method,
            List<string> conflicts)
        {
            if (methods.ContainsKey(name))
            {
                conflicts.Add($"{name} (overwritten by {type.Name}.{method.Name})");
            }
            methods[name] = del;
            metadata[name] = info;
        }

        /// <summary>
        /// Infer a category from the class name (e.g., "WallMethods" → "Wall").
        /// </summary>
        private static string InferCategory(string typeName)
        {
            if (typeName.EndsWith("Methods"))
                return typeName.Substring(0, typeName.Length - "Methods".Length);
            return typeName;
        }
    }
}
