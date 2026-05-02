using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection.Emit;
using System.Text;
using System.Threading.Tasks;
using Terraria;
using Terraria.Audio;
using Terraria.Enums;
using Terraria.GameContent;
using Terraria.GameContent.Drawing;
using Terraria.GameInput;
using Terraria.ID;
using Terraria.UI;
using Terraria.UI.Chat;
using Terraria.UI.Gamepad;
using Terraria.Utilities;

namespace PickReforge
{
    public struct PrefixInfo
    {
        public int ID;
        public int Rarity;
        public float Value;
        public float Scale;

        public PrefixInfo(int id, int rarity, float value)
        {
            ID = id;
            Rarity = rarity;
            Value = value;
            Scale = 1f;
        }
    }


    [HarmonyPatch(typeof(Main), "DrawInventory")]
    public static class Main_DrawInventory_Patch
    {
        private static readonly AccessTools.FieldRef<int> ReforgeCooldownRef = AccessTools.StaticFieldRefAccess<int>(AccessTools.Field(typeof(Main), "reforgeCooldown"));

        private static readonly Action ReforgeItemDelegate = AccessTools.MethodDelegate<Action>(AccessTools.Method(typeof(Main), "ReforgeItemInReforgeSlot"));

        private static int lastItemType = -1;
        private static List<PrefixInfo> validPrefixes = new List<PrefixInfo>();
        private static HashSet<int> selectedPrefixes = new HashSet<int>();
        private static int currentPrefix = 0;

        private static int hoveredPrefix = -1;
        private static float visualScrollY = 0f;
        private static int targetScrollIndex = 0;
        private static float scrollVelocity = 0f;

        private const int LineHeight = 31;
        public static int VisibleLines = 9;
        public static bool CustomCost = true;

        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            CodeMatcher matcher = new CodeMatcher(instructions);

            // Find start of if (Main.InReforgeMenu) block and select first line
            matcher.MatchStartForward(
                new CodeMatch(OpCodes.Ldsfld, AccessTools.Field(typeof(Main), nameof(Main.InReforgeMenu))),
                new CodeMatch(OpCodes.Brfalse)
            );

            if (matcher.IsInvalid) return instructions;

            matcher.Advance(2);
            int startPos = matcher.Pos;

            // Find end of the block (the Br instruction that jumps past else if)
            matcher.MatchStartForward(
                new CodeMatch(OpCodes.Ldsfld, AccessTools.Field(typeof(Main), nameof(Main.InGuideCraftMenu))),
                new CodeMatch(OpCodes.Brfalse)
            );

            while (matcher.Opcode != OpCodes.Br)
            {
                matcher.Advance(-1);
            }

            int endPos = matcher.Pos;

            // Remove the instructions between startPos and endPos, which is the entire if block
            matcher.Start()
                .Advance(startPos)
                .RemoveInstructions(endPos - startPos);

            return matcher.Instructions();
        }

        // Here I'm mostly only commenting my changes, not the vanilla code. The vanilla code is mostly unchanged, just moved around and with some variable name changes for clarity.
        [HarmonyPostfix]
        public static void DrawInventory_Postfix(Main __instance)
        {
            if (!Main.InReforgeMenu) return;

            if (Main.mouseReforge)
            {
                if (Main.reforgeScale < 1f)
                {
                    Main.reforgeScale += 0.02f;
                }
            }
            else
            {
                ReforgeCooldownRef() = 0;
                if (Main.reforgeScale > 0.8f)
                {
                    Main.reforgeScale -= 0.02f;
                }
            }

            // If the player opens a chest, shop, walks too far, or in the guide crafting menu, close reforge menu and clear reforge slot (vanilla logic)
            if (Main.player[Main.myPlayer].chest != -1 || Main.npcShop != 0 || Main.player[Main.myPlayer].talkNPC == -1 || Main.InGuideCraftMenu)
            {
                Main.InReforgeMenu = false;
                Main.player[Main.myPlayer].dropItemCheck();
            }
            else
            {
                int reforgeMenuX = 50;
                int reforgeMenuY = 270;
                string text = Lang.inter[46].Value + ": ";
                if (Main.reforgeItem.type > 0)
                {
                    // If the item in the reforge slot changes, update the list of valid prefixes and selected prefixes
                    if (Main.reforgeItem.type != lastItemType)
                    {
                        lastItemType = Main.reforgeItem.type;
                        validPrefixes.Clear();
                        for (int i = 0; i < PrefixID.Count; i++)
                        {
                            if (Main.reforgeItem.GetRollablePrefixes().Contains(i))
                            {
                                Main.reforgeItem.TryGetPrefixStatMultipliersForItem(i, out _, out _, out _, out _, out _, out _, out _, out _, out _, out float multiplier);
                                int r = 3;
                                if (multiplier >= 1.20f)
                                    r = 5;
                                else if (multiplier >= 1.05f)
                                    r = 4;
                                else if (multiplier <= 0.95f)
                                    r = 2;
                                else if (multiplier <= 0.80f)
                                    r = 1;

                                validPrefixes.Add(new PrefixInfo(i, r, multiplier));
                                // Keep track of the current prefix so we can show it in the UI
                                if (Main.reforgeItem.prefix == i)
                                {
                                    currentPrefix = i;
                                }
                            }
                        }

                        validPrefixes.Sort(delegate (PrefixInfo a, PrefixInfo b)
                        {
                            return b.Value.CompareTo(a.Value);
                        });
                    }
                    long reforgeCost = 0;
                    // Custom reforge cost if prefixes selected and not all prefixes selected
                    if (selectedPrefixes.Count > 0 && selectedPrefixes.Count < validPrefixes.Count && CustomCost)
                    {
                        // We get the base value without prefixes then do the usual reforge cost calculations
                        long baseValue = ContentSamples.ItemsByType[Main.reforgeItem.type].value;
                        long baseCost = baseValue * Main.reforgeItem.stack;
                        if (Main.player[Main.myPlayer].discountAvailable)
                        {
                            baseCost = (long)(baseCost * 0.8);
                        }
                        baseCost = (long)(baseCost * Main.player[Main.myPlayer].currentShoppingSettings.PriceAdjustment);
                        baseCost /= 3;
                        // Now we multiply the cost by the average of the selected prefix multipliers, then multiply by the ratio of valid prefixes to selected prefixes to get the final cost. (Cheaper to select more)
                        float biggestMult = 0f;
                        foreach (PrefixInfo prefix in validPrefixes)
                        {
                            if (!selectedPrefixes.Contains(prefix.ID)) continue;

                            if (prefix.Value > biggestMult)
                            {
                                biggestMult = prefix.Value;
                            }
                        }
                        biggestMult *= biggestMult;
                        biggestMult *= 0.35f;
                        reforgeCost = (long)(baseCost * biggestMult);
                        reforgeCost = (long)(reforgeCost * (float)validPrefixes.Count / selectedPrefixes.Count);
                    }
                    // Vanilla logic
                    else
                    {
                        reforgeCost = (long)Main.reforgeItem.value * (long)Main.reforgeItem.stack;
                        if (Main.player[Main.myPlayer].discountAvailable)
                        {
                            reforgeCost = (long)(reforgeCost * 0.8);
                        }
                        reforgeCost = (long)(reforgeCost * Main.player[Main.myPlayer].currentShoppingSettings.PriceAdjustment);
                        reforgeCost /= 3L;
                    }
                    string totalCostText = "";
                    long platinumCost = 0L;
                    long goldCost = 0L;
                    long silverCost = 0L;
                    long copperCost = 0L;
                    if (reforgeCost < 1L)
                    {
                        reforgeCost = 1L;
                    }

                    long reforgeCostToConvert = reforgeCost;

                    if (reforgeCostToConvert >= 1000000L)
                    {
                        platinumCost = reforgeCostToConvert / 1000000L;
                        reforgeCostToConvert -= platinumCost * 1000000L;
                    }
                    if (reforgeCostToConvert >= 10000L)
                    {
                        goldCost = reforgeCostToConvert / 10000L;
                        reforgeCostToConvert -= goldCost * 10000L;
                    }
                    if (reforgeCostToConvert >= 100L)
                    {
                        silverCost = reforgeCostToConvert / 100L;
                        reforgeCostToConvert -= silverCost * 100L;
                    }
                    if (reforgeCostToConvert >= 1L)
                    {
                        copperCost = reforgeCostToConvert;
                    }
                    if (platinumCost > 0L) totalCostText += $"[c/{Colors.AlphaDarken(Colors.CoinPlatinum).Hex3()}:{platinumCost} {Lang.inter[15].Value}] ";
                    if (goldCost > 0L) totalCostText += $"[c/{Colors.AlphaDarken(Colors.CoinGold).Hex3()}:{goldCost} {Lang.inter[16].Value}] ";
                    if (silverCost > 0L) totalCostText += $"[c/{Colors.AlphaDarken(Colors.CoinSilver).Hex3()}:{silverCost} {Lang.inter[17].Value}] ";
                    if (copperCost > 0L) totalCostText += $"[c/{Colors.AlphaDarken(Colors.CoinCopper).Hex3()}:{copperCost} {Lang.inter[18].Value}] ";
                    ItemSlot.DrawSavings(Main.spriteBatch, (reforgeMenuX + 130), __instance.invBottom, true);
                    ChatManager.DrawColorCodedStringWithShadow(Main.spriteBatch, FontAssets.MouseText.Value, totalCostText, 
                        new Vector2(reforgeMenuX + 50 + FontAssets.MouseText.Value.MeasureString(text).X, reforgeMenuY), Color.White, 0f, Vector2.Zero, Vector2.One, -1f, 2f);

                    // Draw prefix list
                    int listX = reforgeMenuX + 110;
                    int listY = reforgeMenuY + 30;

                    // Handle scrolling with mouse wheel if hovering over the list
                    if (Main.mouseX > listX && Main.mouseX < listX + 200 && Main.mouseY > listY && Main.mouseY < listY + (VisibleLines * LineHeight))
                    {
                        targetScrollIndex -= PlayerInput.ScrollWheelDelta / 120;
                        // Clamp scroll offset to valid range
                        targetScrollIndex = Math.Max(0, Math.Min(targetScrollIndex, Math.Max(0, validPrefixes.Count - (VisibleLines / 2))));
                    }

                    float destinationY = targetScrollIndex * LineHeight;
                    float distance = destinationY - visualScrollY;

                    if (Math.Abs(distance) > scrollVelocity)
                    {
                        scrollVelocity += distance * 0.2f;
                        scrollVelocity *= 0.55f;
                        visualScrollY += scrollVelocity;
                    }
                    else
                    {
                        visualScrollY = destinationY;
                        scrollVelocity = 0f;
                    }

                    // Loop through valid prefixes and draw them starting from scrollOffset until visibleCount
                    for (int i = 0; i < validPrefixes.Count; i++)
                    {
                        // Make copy of struct
                        PrefixInfo prefix = validPrefixes[i];

                        float prefixYPos = listY + ((i + 2) * LineHeight) - visualScrollY;

                        if (prefixYPos < listY - 6 || prefixYPos >= listY + 6 + ((VisibleLines - 1) * LineHeight)) continue;

                        float distFromTop = prefixYPos - listY;
                        float distFromBottom = listY + ((VisibleLines - 1) * LineHeight) - prefixYPos;
                        float distFromEdge = Math.Min(distFromTop, distFromBottom);

                        float alpha = 0f;
                        float buttonAlpha = 0.75f;

                        if (distFromEdge >= LineHeight * 2)
                        {
                            alpha = 1f;
                        }
                        else if (distFromEdge >= LineHeight)
                        {
                            alpha = 0.65f + 0.35f * ((distFromEdge - LineHeight) / LineHeight);
                        }
                        else if (distFromEdge >= 0)
                        {
                            alpha = 0.2f + 0.45f * (distFromEdge / LineHeight);
                        }
                        else if (distFromEdge >= -6)
                        {
                            alpha = 0.2f * (distFromEdge + (LineHeight / 4)) / (LineHeight / 4);
                        }
                        bool isInteractable = alpha > 0.5f;

                        float nameWidth = FontAssets.MouseText.Value.MeasureString(Lang.prefix[prefix.ID].Value).X;
                        int buttonWidth = (int)Math.Max(85, nameWidth + 40);
                        int buttonHeight = LineHeight - 4;

                        Rectangle buttonRect = new Rectangle(listX - 5, (int)prefixYPos, buttonWidth, buttonHeight);
                        bool selected = selectedPrefixes.Contains(prefix.ID);
                        Texture2D buttonTex = selected ? TextureAssets.InventoryBack13.Value : TextureAssets.InventoryBack.Value;
                        Color buttonColor = selected ? Main.OurFavoriteColor : Color.White;
                        Color textColor = Color.White;
                        if (prefix.Rarity == 5)
                            textColor = new Color(150, 255, 150);
                        else if (prefix.Rarity == 4)
                            textColor = new Color(150, 150, 255);
                        else if (prefix.Rarity == 2)
                            textColor = new Color(130, 130, 130);
                        else if (prefix.Rarity == 1)
                            textColor = new Color(130, 130, 130);

                        string prefixText = Lang.prefix[prefix.ID].Value;
                        // If hovering over the prefix, increase scale and play sound on hover, and if clicking select/deselect the prefix
                        if (isInteractable && buttonRect.Contains(Main.mouseX, Main.mouseY) && !PlayerInput.IgnoreMouseInterface)
                        {
                            if (prefix.Scale < 1.2f)
                                prefix.Scale += 0.02f;

                            if (hoveredPrefix != prefix.ID)
                            {
                                SoundEngine.PlaySound(12, -1, -1, 1, 1f, 0f);
                                hoveredPrefix = prefix.ID;
                            }
                            buttonTex = TextureAssets.InventoryBack15.Value;
                            Main.player[Main.myPlayer].mouseInterface = true;
                            if (Main.mouseLeftRelease && Main.mouseLeft)
                            {
                                if (selectedPrefixes.Contains(prefix.ID)) selectedPrefixes.Remove(prefix.ID);
                                else selectedPrefixes.Add(prefix.ID);
                            }
                        }
                        else
                        {
                            textColor *= alpha;
                            buttonColor *= buttonAlpha * alpha;
                            if (prefix.Scale > 1f)
                                prefix.Scale -= 0.02f;
                        }
                        // Overwrite old struct
                        validPrefixes[i] = prefix;

                        buttonRect.Y = buttonRect.Y - (int)((prefix.Scale - 1) * 15f); 
                        buttonRect.Width = (int)(buttonRect.Width * prefix.Scale);
                        buttonRect.Height = (int)(buttonRect.Height * prefix.Scale);
                        // Draw the prefix text with the appropriate color and scale
                        Draw3SliceButton(Main.spriteBatch, buttonTex, buttonRect, buttonColor);
                        ChatManager.DrawColorCodedStringWithShadow(Main.spriteBatch, FontAssets.MouseText.Value, prefixText, 
                            new Vector2(listX + 4, (prefixYPos + 3) - (prefix.Scale - 1) * 15f), textColor, 0f, Vector2.Zero, Vector2.One * prefix.Scale, -1f, 2f);
                    }

                    int buttonX = reforgeMenuX + 70;
                    int buttonY = reforgeMenuY + 40;
                    bool isHovering = Main.mouseX > buttonX - 15 && Main.mouseX < buttonX + 15 && Main.mouseY > buttonY - 15 && Main.mouseY < buttonY + 15 && !PlayerInput.IgnoreMouseInterface;
                    Texture2D texture2D3 = isHovering ? texture2D3 = TextureAssets.Reforge[1].Value : TextureAssets.Reforge[0].Value;
                    Main.spriteBatch.Draw(texture2D3, new Vector2(buttonX, buttonY), null, Color.White, 0f, texture2D3.Size() / 2f, Main.reforgeScale, SpriteEffects.None, 0f);
                    UILinkPointNavigator.SetPosition(304, new Vector2(buttonX, buttonY) + texture2D3.Size() / 4f);
                    if (isHovering)
                    {
                        Main.hoverItemName = Lang.inter[19].Value;
                        if (!Main.mouseReforge)
                        {
                            SoundEngine.PlaySound(12, -1, -1, 1, 1f, 0f);
                        }
                        Main.mouseReforge = true;
                        Main.player[Main.myPlayer].mouseInterface = true;
                        if (Main.mouseLeftRelease && Main.mouseLeft && ReforgeCooldownRef() <= 0 && Main.player[Main.myPlayer].BuyItem(reforgeCost, -1))
                        {
                            if (selectedPrefixes.Count > 0)
                            {
                                ReforgeItemInSlotMod(selectedPrefixes);
                            } 
                            else
                            {
                                ReforgeItemDelegate();
                            }
                        }
                    }
                    else
                    {
                        Main.mouseReforge = false;
                    }
                }
                else
                {
                    text = Lang.inter[20].Value;
                }
                ChatManager.DrawColorCodedStringWithShadow(Main.spriteBatch, FontAssets.MouseText.Value, text, 
                    new Vector2((reforgeMenuX + 50), reforgeMenuY), 
                    new Color(Main.mouseTextColor, Main.mouseTextColor, Main.mouseTextColor, Main.mouseTextColor), 0f, Vector2.Zero, Vector2.One, -1f, 2f);
                if (Main.mouseX >= reforgeMenuX && Main.mouseX <= reforgeMenuX + TextureAssets.InventoryBack.Width() * Main.inventoryScale && 
                    Main.mouseY >= reforgeMenuY && Main.mouseY <= reforgeMenuY + TextureAssets.InventoryBack.Height() * Main.inventoryScale && !PlayerInput.IgnoreMouseInterface)
                {
                    Main.craftingHide = true;
                    Main.player[Main.myPlayer].mouseInterface = true;
                    ItemSlot.Handle(ref Main.reforgeItem, 5, true);
                }
                ItemSlot.Draw(Main.spriteBatch, ref Main.reforgeItem, 5, new Vector2(reforgeMenuX, reforgeMenuY), default(Color));
            }
        }

        private static void ReforgeItemInSlotMod(HashSet<int> prefixes)
        {
            Main.reforgeItem.ResetPrefix();
            UnifiedRandom rand = new UnifiedRandom();
            int randomIndex = Main.rand.Next(prefixes.Count);
            int current = 0;
            bool best = false;
            foreach (int id in prefixes)
            {
                if (current == randomIndex)
                    Main.reforgeItem.Prefix(id, out best);
                current++;
            }
            PopupText.NewText(PopupTextContext.ItemReforge, Main.reforgeItem, Main.LocalPlayer.Center, Main.reforgeItem.stack, true, false);
            SoundEngine.PlaySound(SoundID.Item37, -1, -1, 0f, 1f);
        }

        public static void Draw3SliceButton(SpriteBatch spriteBatch, Texture2D tex, Rectangle rect, Color color)
        {
            float texRatio = tex.Width / (float)rect.Height;

            int leftCapWidth = rect.Height / 2;
            int rightCapWidth = rect.Height - leftCapWidth;

            if (leftCapWidth + rightCapWidth >= rect.Height) rightCapWidth -= 1;

            int texLeftCapWidth = (int)(leftCapWidth * texRatio);
            int texRightCapWidth = (int)(rightCapWidth * texRatio);

            // Left cap
            spriteBatch.Draw(tex, 
                new Rectangle(rect.X, rect.Y, leftCapWidth, rect.Height), 
                new Rectangle(0, 0, texLeftCapWidth, tex.Height), color);
            // Middle
            spriteBatch.Draw(tex,
                new Rectangle(rect.X + leftCapWidth, rect.Y, rect.Width - leftCapWidth - rightCapWidth, rect.Height), 
                new Rectangle(texLeftCapWidth, 0, 1, tex.Height), color);
            // Right cap
            spriteBatch.Draw(tex, 
                new Rectangle(rect.Right - rightCapWidth, rect.Y, rightCapWidth, rect.Height), 
                new Rectangle(tex.Width - texRightCapWidth, 0, texRightCapWidth, tex.Height), color);
        }
    }
}
