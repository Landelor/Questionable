using System;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Microsoft.Extensions.Logging;

namespace Questionable.Functions;

internal sealed class GearFunctions
{
    private readonly ILogger<GearFunctions> _logger;

    public GearFunctions(ILogger<GearFunctions> logger)
    {
        _logger = logger;
    }

    public unsafe int GetLowestGearDurabilityPercentage()
    {
        try
        {
            var inventoryManager = InventoryManager.Instance();
            if (inventoryManager == null)
                return 100;

            int lowestDurability = 100;

            // Check all equipped gear slots
            var equippedContainer = inventoryManager->GetInventoryContainer(InventoryType.EquippedItems);
            if (equippedContainer == null)
                return 100;

            for (int i = 0; i < equippedContainer->Size; ++i)
            {
                var item = equippedContainer->GetInventorySlot(i);
                if (item == null || item->ItemId == 0)
                    continue;

                // Calculate durability percentage
                if (item->Condition > 0)
                {
                    var durabilityPercentage = (int)Math.Round((double)item->Condition / 30000 * 100);
                    if (durabilityPercentage < lowestDurability)
                    {
                        lowestDurability = durabilityPercentage;
                        _logger.LogDebug("Item in slot {Slot} has {Durability}% durability", i, durabilityPercentage);
                    }
                }
            }

            _logger.LogDebug("Lowest gear durability: {LowestDurability}%", lowestDurability);
            return lowestDurability;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check gear durability");
            return 100; // Default to 100% if we can't check
        }
    }

    public bool NeedsRepair(int threshold)
    {
        var lowestDurability = GetLowestGearDurabilityPercentage();
        var needsRepair = lowestDurability <= threshold;

        if (needsRepair)
            _logger.LogInformation("Gear needs repair: {LowestDurability}% <= {Threshold}%", lowestDurability, threshold);

        return needsRepair;
    }

    public unsafe bool HasDarkMatter()
    {
        try
        {
            var inventoryManager = InventoryManager.Instance();
            if (inventoryManager == null)
                return false;

            // Check main inventory for Dark Matter (Grade 8 = 33916, Grade 7 = 10386, etc.)
            // We'll check for the most common ones
            uint[] darkMatterIds = [33916, 10386, 5594, 5593, 5592, 5591]; // Grade 8 down to Grade 3

            var containers = new[]
            {
                InventoryType.Inventory1,
                InventoryType.Inventory2,
                InventoryType.Inventory3,
                InventoryType.Inventory4
            };

            foreach (var containerType in containers)
            {
                var container = inventoryManager->GetInventoryContainer(containerType);
                if (container == null) continue;

                for (int i = 0; i < container->Size; ++i)
                {
                    var item = container->GetInventorySlot(i);
                    if (item == null || item->ItemId == 0) continue;

                    if (darkMatterIds.Contains(item->ItemId) && item->Quantity > 0)
                    {
                        _logger.LogDebug("Found Dark Matter (ID: {ItemId}) x{Quantity}", item->ItemId, item->Quantity);
                        return true;
                    }
                }
            }

            _logger.LogDebug("No Dark Matter found in inventory");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check for Dark Matter");
            return false;
        }
    }
}