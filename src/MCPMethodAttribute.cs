using System;

namespace RevitMCPBridge
{
    /// <summary>
    /// Marks a static method for automatic registration in the MCP method registry.
    /// The method must have signature: public static string Name(UIApplication, JObject)
    ///
    /// At startup, MCPMethodScanner discovers all tagged methods via reflection and
    /// registers them as direct Func delegates — no reflection at dispatch time.
    ///
    /// Usage:
    ///   [MCPMethod("createWall", "createWallByPoints")]
    ///   public static string CreateWallByPoints(UIApplication uiApp, JObject parameters) { ... }
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class MCPMethodAttribute : Attribute
    {
        /// <summary>
        /// The primary MCP method name clients use to call this method.
        /// Case-insensitive at dispatch time.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Additional names that route to the same method (e.g., "createWall" → CreateWallByPoints).
        /// </summary>
        public string[] Aliases { get; }

        /// <summary>
        /// Category for documentation and discovery (e.g., "Wall", "Sheet", "MEP").
        /// Used by getMethods/listMethods API responses.
        /// </summary>
        public string Category { get; set; }

        /// <summary>
        /// Brief description for API discovery.
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// Mark a method for auto-registration with the MCP server.
        /// </summary>
        /// <param name="name">Primary method name</param>
        /// <param name="aliases">Optional alternate names that route to this method</param>
        public MCPMethodAttribute(string name, params string[] aliases)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Aliases = aliases ?? Array.Empty<string>();
        }
    }
}
