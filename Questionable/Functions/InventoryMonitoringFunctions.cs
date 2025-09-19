using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Microsoft.Extensions.Logging;

namespace Questionable.Functions;

internal sealed unsafe class InventoryMonitoringFunctions
{
    private readonly CofferFunctions _cofferFunctions;
    private readonly ILogger<InventoryMonitoringFunctions> _logger;

    private Dictionary<uint, int> _baselineInventory = new();
    private Dictionary<uint, int> _lastKnownInventory = new();
    private bool _isMonitoring;

    public InventoryMonitoringFunctions(
        CofferFunctions cofferFunctions,
        ILogger<InventoryMonitoringFunctions> logger)
    {
        _cofferFunctions = cofferFunctions;
        _logger = logger;
    }

    /// <summary>
    /// Starts monitoring inventory changes by capturing the current baseline state.
    /// </summary>
    public void StartMonitoring()
    {
        try
        {
            _baselineInventory = GetCurrentInventorySnapshot();
            _lastKnownInventory = new Dictionary<uint, int>(_baselineInventory);
            _isMonitoring = true;

            _logger.LogInformation("Started inventory monitoring with {ItemCount} baseline items",
                _baselineInventory.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start inventory monitoring");
            _isMonitoring = false;
        }
    }

    /// <summary>
    /// Stops inventory monitoring and clears baseline data.
    /// </summary>
    public void StopMonitoring()
    {
        _isMonitoring = false;
        _baselineInventory.Clear();
        _lastKnownInventory.Clear();

        _logger.LogInformation("Stopped inventory monitoring");
    }

    /// <summary>
    /// Gets all items that have been added to inventory since monitoring started.
    /// </summary>
    /// <returns>Dictionary of item ID to quantity gained</returns>
    public Dictionary<uint, int> GetNewlyAcquiredItems()
    {
        if (!_isMonitoring)
        {
            _logger.LogWarning("Attempted to get newly acquired items while not monitoring");
            return new Dictionary<uint, int>();
        }

        try
        {
            var currentInventory = GetCurrentInventorySnapshot();
            var newlyAcquired = new Dictionary<uint, int>();

            foreach (var (itemId, currentCount) in currentInventory)
            {
                var baselineCount = _baselineInventory.GetValueOrDefault(itemId, 0);
                var gainedCount = currentCount - baselineCount;

                if (gainedCount > 0)
                {
                    newlyAcquired[itemId] = gainedCount;
                }
            }

            return newlyAcquired;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get newly acquired items");
            return new Dictionary<uint, int>();
        }
    }

    /// <summary>
    /// Gets all coffers that have been added to inventory since monitoring started.
    /// </summary>
    /// <returns>Dictionary of coffer item ID to quantity gained</returns>
    public Dictionary<uint, int> GetNewlyAcquiredCoffers()
    {
        var newlyAcquired = GetNewlyAcquiredItems();
        var newCoffers = new Dictionary<uint, int>();

        foreach (var (itemId, quantity) in newlyAcquired)
        {
            if (_cofferFunctions.IsCoffer(itemId))
            {
                newCoffers[itemId] = quantity;
                _logger.LogDebug("Detected new coffer: {CofferName} (ID: {ItemId}) x{Quantity}",
                    _cofferFunctions.GetCofferName(itemId), itemId, quantity);
            }
        }

        return newCoffers;
    }

    /// <summary>
    /// Checks if any coffers have been added since the last check and updates tracking.
    /// </summary>
    /// <returns>Dictionary of newly detected coffers since last check</returns>
    public Dictionary<uint, int> GetNewCoffersSinceLastCheck()
    {
        if (!_isMonitoring)
        {
            return new Dictionary<uint, int>();
        }

        try
        {
            var currentInventory = GetCurrentInventorySnapshot();
            var newCoffers = new Dictionary<uint, int>();

            foreach (var (itemId, currentCount) in currentInventory)
            {
                var lastKnownCount = _lastKnownInventory.GetValueOrDefault(itemId, 0);
                var gainedCount = currentCount - lastKnownCount;

                if (gainedCount > 0 && _cofferFunctions.IsCoffer(itemId))
                {
                    newCoffers[itemId] = gainedCount;
                    _logger.LogInformation("New coffer detected: {CofferName} (ID: {ItemId}) x{Quantity}",
                        _cofferFunctions.GetCofferName(itemId), itemId, gainedCount);
                }
            }

            // Update last known inventory state
            _lastKnownInventory = new Dictionary<uint, int>(currentInventory);

            return newCoffers;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check for new coffers");
            return new Dictionary<uint, int>();
        }
    }

    /// <summary>
    /// Gets the current quantity of a specific item in inventory.
    /// </summary>
    /// <param name="itemId">The item ID to check</param>
    /// <returns>Current quantity of the item</returns>
    public int GetCurrentItemCount(uint itemId)
    {
        try
        {
            InventoryManager* inventoryManager = InventoryManager.Instance();
            if (inventoryManager == null)
            {
                _logger.LogWarning("InventoryManager is null when checking item count for {ItemId}", itemId);
                return 0;
            }

            return inventoryManager->GetInventoryItemCount(itemId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get current item count for {ItemId}", itemId);
            return 0;
        }
    }

    /// <summary>
    /// Checks if monitoring is currently active.
    /// </summary>
    public bool IsMonitoring => _isMonitoring;

    /// <summary>
    /// Gets the baseline inventory count for a specific item.
    /// </summary>
    /// <param name="itemId">The item ID to check</param>
    /// <returns>Baseline count, or 0 if not in baseline or not monitoring</returns>
    public int GetBaselineItemCount(uint itemId)
    {
        if (!_isMonitoring)
            return 0;

        return _baselineInventory.GetValueOrDefault(itemId, 0);
    }

    /// <summary>
    /// Creates a complete snapshot of all items in the player's main inventory.
    /// </summary>
    /// <returns>Dictionary mapping item ID to quantity</returns>
    private Dictionary<uint, int> GetCurrentInventorySnapshot()
    {
        var inventory = new Dictionary<uint, int>();

        InventoryManager* inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
        {
            _logger.LogWarning("InventoryManager is null when creating inventory snapshot");
            return inventory;
        }

        // Check main inventory containers (Inventory1-4)
        for (InventoryType inventoryType = InventoryType.Inventory1;
             inventoryType <= InventoryType.Inventory4;
             ++inventoryType)
        {
            InventoryContainer* inventoryContainer = inventoryManager->GetInventoryContainer(inventoryType);
            if (inventoryContainer == null)
                continue;

            for (int i = 0; i < inventoryContainer->Size; ++i)
            {
                InventoryItem* item = inventoryContainer->GetInventorySlot(i);
                if (item == null || item->ItemId == 0)
                    continue;

                uint itemId = item->ItemId;
                int quantity = item->Quantity;

                if (inventory.ContainsKey(itemId))
                    inventory[itemId] += quantity;
                else
                    inventory[itemId] = quantity;
            }
        }

        // Also check key items and other special inventories that might contain coffers
        var additionalInventories = new[]
        {
            InventoryType.KeyItems,
            InventoryType.ArmoryMainHand,
            InventoryType.ArmoryOffHand,
            InventoryType.ArmoryHead,
            InventoryType.ArmoryBody,
            InventoryType.ArmoryHands,
            InventoryType.ArmoryLegs,
            InventoryType.ArmoryFeets,
            InventoryType.ArmoryEar,
            InventoryType.ArmoryNeck,
            InventoryType.ArmoryWrist,
            InventoryType.ArmoryRings
        };

        foreach (var inventoryType in additionalInventories)
        {
            InventoryContainer* inventoryContainer = inventoryManager->GetInventoryContainer(inventoryType);
            if (inventoryContainer == null)
                continue;

            for (int i = 0; i < inventoryContainer->Size; ++i)
            {
                InventoryItem* item = inventoryContainer->GetInventorySlot(i);
                if (item == null || item->ItemId == 0)
                    continue;

                uint itemId = item->ItemId;
                int quantity = item->Quantity;

                if (inventory.ContainsKey(itemId))
                    inventory[itemId] += quantity;
                else
                    inventory[itemId] = quantity;
            }
        }

        // Debug logging removed to prevent spam - snapshot creation is a frequent operation
        return inventory;
    }

    /// <summary>
    /// Gets a summary of inventory changes since monitoring started.
    /// </summary>
    /// <returns>Formatted string describing changes</returns>
    public string GetInventoryChangesSummary()
    {
        if (!_isMonitoring)
            return "Inventory monitoring is not active.";

        var newItems = GetNewlyAcquiredItems();
        var newCoffers = GetNewlyAcquiredCoffers();

        if (newItems.Count == 0)
            return "No new items detected since monitoring started.";

        var summary = $"Detected {newItems.Count} new item types";
        if (newCoffers.Count > 0)
        {
            var cofferNames = newCoffers.Select(kvp =>
                $"{_cofferFunctions.GetCofferName(kvp.Key)} x{kvp.Value}");
            summary += $", including {newCoffers.Count} coffer(s): {string.Join(", ", cofferNames)}";
        }

        return summary;
    }
}