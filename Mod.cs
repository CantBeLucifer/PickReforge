using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Terraria;
using Terraria.DataStructures;
using TerrariaModder.Core;
using TerrariaModder.Core.Config;
using TerrariaModder.Core.Logging;

namespace PickReforge
{
    public class FirstModConfig : ModConfig
    {
        public override int Version => 1;

        [Client, Label("List Length"), Description("Length of scrollable Prefix list"), Range(9, 19)]
        public int ListLength { get; set; } = 9;

        [Client, Label("Custom Cost"), Description("Whether to calculate 'fair' cost for reforging or use vanilla")]
        public bool CustomCost { get; set; } = true;
    }

    public class Mod : IMod, IModLifecycle
    {
        public string Id => "pick-reforge";
        public string Name => "Pick Reforge";
        public string Version => "1.0.0";

        private ILogger _log;
        private ModContext _context;
        private FirstModConfig _config;

        public void Initialize(ModContext context)
        {
            _log = context.Logger;
            _context = context;
            _config = context.GetConfig<FirstModConfig>();

            LoadConfig();

            _log.Info("Pick Reforge initialized!");
        }

        private void LoadConfig()
        {
            if (_config == null) return;
            Main_DrawInventory_Patch.VisibleLines = _config.ListLength;
            Main_DrawInventory_Patch.CustomCost = _config.CustomCost;
        }

        // Optional: Implement this to receive config changes without restart
        public void OnConfigChanged()
        {
            LoadConfig();
            // Re-read any cached config values here
        }

        public void OnContentReady(ModContext context)
        {
            // Called after all mods are initialized and content IDs are assigned.
            // Use this for cross-mod lookups or anything that depends on other mods being loaded.
        }

        public void OnWorldLoad() { }

        public void OnWorldUnload() { }

        public void Unload()
        {
            _log.Info("Pick Reforge unloading");
        }
    }
}
