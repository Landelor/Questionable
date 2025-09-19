using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using Questionable.Functions;
using Questionable.Model.Questing;

namespace Questionable.Controller;

/// <summary>
/// Controller responsible for automatically opening coffers after quest completion.
/// Monitors inventory for newly acquired coffers and opens them after quest completion when safe to do so.
/// </summary>
internal sealed class CofferController : IDisposable
{
    private readonly Configuration _configuration;
    private readonly CofferFunctions _cofferFunctions;
    private readonly InventoryMonitoringFunctions _inventoryMonitoringFunctions;
    private readonly GameFunctions _gameFunctions;
    private readonly ICondition _condition;
    private readonly IFramework _framework;
    private readonly ILogger<CofferController> _logger;

    private readonly Queue<PendingCoffer> _pendingCoffers = new();
    private readonly HashSet<uint> _processedCofferInstances = new();
    private readonly Dictionary<uint, int> _lastKnownCofferCounts = new();
    private DateTime _lastCofferCheck = DateTime.MinValue;
    private DateTime _lastCofferUse = DateTime.MinValue;
    private bool _isRunning;
    private bool _isWaitingForQuestCompletion;
    private bool _hasPendingCoffersFromQuest;
    private bool _isWaitingForCasting;
    private DateTime _castingStartedAt = DateTime.MinValue;
    private ElementId? _expectedCompletionQuestId;
    private ElementId? _lastProcessedQuestId;
    private DateTime _lastQuestCompletionTime = DateTime.MinValue;
    private DateTime _lastInventoryProcessingStart = DateTime.MinValue;

    // Coffer consumption verification state
    private uint _lastUsedCofferItemId; // Item ID of the last coffer we attempted to open
    private int _preUseCofferCount; // Count of this coffer before using it
    private DateTime _consumptionVerificationStartTime = DateTime.MinValue; // When we started verification
    private bool _isVerifyingConsumption; // Whether we're currently verifying consumption
    private int _consumptionRetryCount; // Number of retry attempts for the current coffer
    private int _consumptionCheckCount; // Number of consumption checks performed
    private DateTime _lastConsumptionCheck = DateTime.MinValue; // Last time we checked consumption

    // Configuration constants
    private const int CofferCheckIntervalMs = 500; // Check for new coffers every 500ms
    private const int CofferUseDelayMs = 250; // Wait 250ms between opening coffers
    private const int InventoryFullRetryDelayMs = 5000; // Wait 5 seconds before retrying when inventory is full
    private const int CastingTimeoutMs = 10000; // Maximum wait time for casting to complete (10 seconds)
    private const int PostCastingDelayMs = 100; // Additional delay after casting completes
    private const int ConsumptionVerificationDelayMs = 1000; // Wait 1000ms before checking consumption
    private const int ConsumptionVerificationTimeoutMs = 5000; // Maximum wait time for consumption verification (5 seconds)
    private const int MaxConsumptionRetries = 2; // Maximum retries for failed consumption verification
    private const int ConsumptionCheckIntervalMs = 500; // Check consumption every 500ms during verification
    private const int MinConsumptionChecks = 3; // Minimum number of checks before giving up

    public CofferController(
        Configuration configuration,
        CofferFunctions cofferFunctions,
        InventoryMonitoringFunctions inventoryMonitoringFunctions,
        GameFunctions gameFunctions,
        ICondition condition,
        IFramework framework,
        ILogger<CofferController> logger)
    {
        _configuration = configuration;
        _cofferFunctions = cofferFunctions;
        _inventoryMonitoringFunctions = inventoryMonitoringFunctions;
        _gameFunctions = gameFunctions;
        _condition = condition;
        _framework = framework;
        _logger = logger;

        _framework.Update += OnFrameworkUpdate;
    }

    /// <summary>
    /// Gets whether the controller is waiting for quest completion.
    /// </summary>
    public bool IsWaitingForQuestCompletion => _isWaitingForQuestCompletion;

    /// <summary>
    /// Gets whether there are pending coffers from the completed quest.
    /// </summary>
    public bool HasPendingCoffersFromQuest => _hasPendingCoffersFromQuest;

    /// <summary>
    /// Gets whether the controller is currently processing inventory coffers.
    /// </summary>
    public bool IsProcessingInventoryCoffers { get; private set; }

    /// <summary>
    /// Prepares the controller for quest completion by starting inventory monitoring.
    /// </summary>
    public void PrepareForQuestCompletion(ElementId questId)
    {
        if (!_configuration.General.AutoOpenCoffers)
        {
            _logger.LogDebug("Auto-open coffers is disabled in configuration");
            return;
        }

        _logger.LogInformation("Preparing CofferController for quest completion: {QuestId}", questId);
        _expectedCompletionQuestId = questId;
        _isWaitingForQuestCompletion = true;
        _hasPendingCoffersFromQuest = false;

        // Start inventory monitoring to track newly acquired coffers
        _inventoryMonitoringFunctions.StartMonitoring();
    }

    /// <summary>
    /// Called when a quest is completed to activate coffer processing.
    /// </summary>
    public void OnQuestCompleted(ElementId questId)
    {
        if (!_configuration.General.AutoOpenCoffers)
            return;

        if (_expectedCompletionQuestId != null && !_expectedCompletionQuestId.Equals(questId))
        {
            _logger.LogWarning("Unexpected quest completion: expected {ExpectedQuestId}, got {ActualQuestId}",
                _expectedCompletionQuestId, questId);
            return;
        }

        _logger.LogInformation("Quest completed, activating CofferController: {QuestId}", questId);
        _isRunning = true;
        _isWaitingForQuestCompletion = false;
        _hasPendingCoffersFromQuest = _pendingCoffers.Count > 0;
        _lastCofferCheck = DateTime.MinValue;
        _lastCofferUse = DateTime.MinValue;
        _expectedCompletionQuestId = null;
        _lastProcessedQuestId = questId;
        _lastQuestCompletionTime = DateTime.Now;
    }

    /// <summary>
    /// Starts the coffer controller manually (for legacy compatibility).
    /// </summary>
    public void Start()
    {
        if (_isRunning)
        {
            _logger.LogDebug("CofferController is already running");
            return;
        }

        if (!_configuration.General.AutoOpenCoffers)
        {
            _logger.LogDebug("Auto-open coffers is disabled in configuration");
            return;
        }

        _logger.LogInformation("Starting CofferController (manual mode)");
        _isRunning = true;
        _isWaitingForQuestCompletion = false;
        _hasPendingCoffersFromQuest = false;
        _pendingCoffers.Clear();
        _lastCofferCheck = DateTime.MinValue;
        _lastCofferUse = DateTime.MinValue;
        _expectedCompletionQuestId = null;

        // Start inventory monitoring to track newly acquired coffers
        _inventoryMonitoringFunctions.StartMonitoring();
    }

    /// <summary>
    /// Stops the coffer controller and clears any pending coffers.
    /// </summary>
    public void Stop(string reason)
    {
        if (!_isRunning && !_isWaitingForQuestCompletion)
            return;

        _logger.LogInformation("Stopping CofferController: {Reason}", reason);
        _isRunning = false;
        _isWaitingForQuestCompletion = false;
        _hasPendingCoffersFromQuest = false;
        _isWaitingForCasting = false;
        _castingStartedAt = DateTime.MinValue;
        _expectedCompletionQuestId = null;
        _lastProcessedQuestId = null;
        _lastQuestCompletionTime = DateTime.MinValue;
        _lastInventoryProcessingStart = DateTime.MinValue;
        IsProcessingInventoryCoffers = false;
        _pendingCoffers.Clear();
        _processedCofferInstances.Clear();
        _lastKnownCofferCounts.Clear();
        _inventoryMonitoringFunctions.StopMonitoring();

        // Reset consumption verification state
        _lastUsedCofferItemId = 0;
        _preUseCofferCount = 0;
        _consumptionVerificationStartTime = DateTime.MinValue;
        _isVerifyingConsumption = false;
        _consumptionRetryCount = 0;
        _consumptionCheckCount = 0;
        _lastConsumptionCheck = DateTime.MinValue;
    }

    /// <summary>
    /// Returns true if the controller is currently running.
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Returns true if the controller has completed processing all coffers from the last quest completion.
    /// </summary>
    public bool IsCompleted => !_isRunning && !_isWaitingForQuestCompletion && _hasPendingCoffersFromQuest && _pendingCoffers.Count == 0;

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!_isRunning || !_configuration.General.AutoOpenCoffers)
            return;

        try
        {
            Update();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in CofferController update");
        }
    }

    private void Update()
    {
        // If we're waiting for quest completion, only monitor for new coffers
        if (_isWaitingForQuestCompletion)
        {
            if (DateTime.Now - _lastCofferCheck > TimeSpan.FromMilliseconds(CofferCheckIntervalMs))
            {
                CheckForNewCoffers();
                _lastCofferCheck = DateTime.Now;

                // Update the flag if we detect coffers while waiting
                if (_pendingCoffers.Count > 0)
                    _hasPendingCoffersFromQuest = true;
            }
            return;
        }

        // Periodic cleanup of old processed coffer instances
        CleanupOldProcessedInstances();

        // Normal processing: check for new coffers and process pending ones
        if (DateTime.Now - _lastCofferCheck > TimeSpan.FromMilliseconds(CofferCheckIntervalMs))
        {
            CheckForNewCoffers();
            _lastCofferCheck = DateTime.Now;
        }

        // Process pending coffers if conditions are safe
        if (_pendingCoffers.Count > 0 && IsSafeToOpenCoffers())
        {
            ProcessPendingCoffers();
        }

        // Stop automatically when all coffers from quest completion are processed
        // and verify no coffers remain in inventory
        if (_hasPendingCoffersFromQuest && _pendingCoffers.Count == 0)
        {
            // Re-scan inventory to make sure no coffers were missed
            var remainingCoffers = GetAllCoffersInInventory();
            if (remainingCoffers.Count > 0)
            {
                _logger.LogInformation("Detected {CofferCount} remaining coffers after queue empty, re-adding to queue", remainingCoffers.Count);

                // Re-add any found coffers to the queue
                foreach (var (itemId, quantity) in remainingCoffers)
                {
                    var cofferName = _cofferFunctions.GetCofferName(itemId);
                    var cofferSlots = GetCofferSlotsInInventory(itemId, quantity);

                    for (int i = 0; i < quantity && i < cofferSlots.Count; i++)
                    {
                        _pendingCoffers.Enqueue(new PendingCoffer
                        {
                            ItemId = itemId,
                            Name = cofferName,
                            AcquiredAt = DateTime.Now,
                            InventoryType = cofferSlots[i].InventoryType,
                            SlotIndex = cofferSlots[i].SlotIndex
                        });
                    }
                }
            }
            else
            {
                _logger.LogInformation("All quest completion coffers processed and no coffers remain in inventory, stopping CofferController");
                IsProcessingInventoryCoffers = false;
                Stop("Quest completion coffers processed");
            }
        }
    }

    private void CheckForNewCoffers()
    {
        if (!_inventoryMonitoringFunctions.IsMonitoring)
            return;

        var newCoffers = _inventoryMonitoringFunctions.GetNewCoffersSinceLastCheck();

        foreach (var (itemId, quantity) in newCoffers)
        {
            var cofferName = _cofferFunctions.GetCofferName(itemId);
            _logger.LogInformation("Detected new coffer: {CofferName} x{Quantity}", cofferName, quantity);

            // Add each coffer instance to the pending queue
            // Note: For newly detected coffers, we'll determine slot positions when processing
            // since slot positions might change between detection and processing
            for (int i = 0; i < quantity; i++)
            {
                _pendingCoffers.Enqueue(new PendingCoffer
                {
                    ItemId = itemId,
                    Name = cofferName,
                    AcquiredAt = DateTime.Now,
                    InventoryType = FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory1, // Will be updated during processing
                    SlotIndex = -1 // Will be updated during processing
                });
            }
        }
    }

    /// <summary>
    /// Processes pending coffers with enhanced consumption verification.
    ///
    /// This method implements a three-stage process:
    /// 1. Cast completion: Wait for UseItem casting to finish
    /// 2. Consumption verification: Verify the coffer was actually removed from inventory
    /// 3. Retry logic: Re-queue coffers that reported success but weren't consumed
    ///
    /// This prevents false positives where UseItem returns success but the server
    /// doesn't actually consume the coffer from inventory.
    /// </summary>
    private void ProcessPendingCoffers()
    {
        // If we're currently waiting for casting to finish, check if it's done
        if (_isWaitingForCasting)
        {
            if (_condition[ConditionFlag.Casting])
            {
                // Still casting, check for timeout
                if (DateTime.Now - _castingStartedAt > TimeSpan.FromMilliseconds(CastingTimeoutMs))
                {
                    _logger.LogWarning("Casting timeout exceeded, resetting casting wait state");
                    _isWaitingForCasting = false;
                    _castingStartedAt = DateTime.MinValue;
                    _isVerifyingConsumption = false;
                    _consumptionVerificationStartTime = DateTime.MinValue;
                }
                return; // Still casting, wait
            }
            else
            {
                // Casting finished, add a small delay to ensure the server has processed the cast
                if (DateTime.Now - _lastCofferUse < TimeSpan.FromMilliseconds(PostCastingDelayMs))
                    return;

                _logger.LogDebug("Casting completed, starting consumption verification");
                _isWaitingForCasting = false;
                _castingStartedAt = DateTime.MinValue;
                _isVerifyingConsumption = true;
                _consumptionVerificationStartTime = DateTime.Now;
                _consumptionCheckCount = 0;
                _lastConsumptionCheck = DateTime.MinValue;
                return; // Start verification process
            }
        }

        // If we're verifying coffer consumption, check if the coffer was actually consumed
        if (_isVerifyingConsumption)
        {
            // Add initial delay before checking consumption
            if (DateTime.Now - _consumptionVerificationStartTime < TimeSpan.FromMilliseconds(ConsumptionVerificationDelayMs))
                return;

            // Only check consumption at intervals to avoid spamming
            if (DateTime.Now - _lastConsumptionCheck < TimeSpan.FromMilliseconds(ConsumptionCheckIntervalMs))
                return;

            _lastConsumptionCheck = DateTime.Now;
            _consumptionCheckCount++;

            var currentCofferCount = _inventoryMonitoringFunctions.GetCurrentItemCount(_lastUsedCofferItemId);
            _logger.LogDebug("Consumption check #{CheckCount}: {CofferName} count is {CurrentCount} (was {PreCount})",
                _consumptionCheckCount, _cofferFunctions.GetCofferName(_lastUsedCofferItemId), currentCofferCount, _preUseCofferCount);

            if (currentCofferCount < _preUseCofferCount)
            {
                // Coffer was successfully consumed
                _logger.LogInformation("Verified coffer consumption after {CheckCount} checks: {CofferName} (count decreased from {PreCount} to {PostCount})",
                    _consumptionCheckCount, _cofferFunctions.GetCofferName(_lastUsedCofferItemId), _preUseCofferCount, currentCofferCount);
                _isVerifyingConsumption = false;
                _consumptionVerificationStartTime = DateTime.MinValue;
                _consumptionRetryCount = 0;
                _consumptionCheckCount = 0;
                _lastConsumptionCheck = DateTime.MinValue;
                // Continue to next coffer
            }
            else if ((DateTime.Now - _consumptionVerificationStartTime > TimeSpan.FromMilliseconds(ConsumptionVerificationTimeoutMs)) &&
                     (_consumptionCheckCount >= MinConsumptionChecks))
            {
                // Timeout - coffer was not consumed, this was a false positive
                _logger.LogWarning("Coffer consumption verification timeout after {CheckCount} checks: {CofferName} was not consumed from inventory (count remained {Count})",
                    _consumptionCheckCount, _cofferFunctions.GetCofferName(_lastUsedCofferItemId), currentCofferCount);

                // Remove the coffer from processed instances so it can be retried
                // Find all possible instance IDs for this coffer and remove them
                var cofferSlots = GetCofferSlotsInInventory(_lastUsedCofferItemId, 10);
                foreach (var slot in cofferSlots)
                {
                    var instanceId = GenerateCofferInstanceIdFromSlot(_lastUsedCofferItemId, slot.InventoryType, slot.SlotIndex);
                    _processedCofferInstances.Remove(instanceId);
                }

                if (_consumptionRetryCount < MaxConsumptionRetries)
                {
                    _logger.LogInformation("Re-queuing coffer for retry (attempt {RetryCount}/{MaxRetries}): {CofferName}",
                        _consumptionRetryCount + 1, MaxConsumptionRetries, _cofferFunctions.GetCofferName(_lastUsedCofferItemId));

                    // Re-queue the coffer for another attempt
                    var firstSlot = FindFirstCofferSlot(_lastUsedCofferItemId);
                    if (firstSlot.HasValue)
                    {
                        _pendingCoffers.Enqueue(new PendingCoffer
                        {
                            ItemId = _lastUsedCofferItemId,
                            Name = _cofferFunctions.GetCofferName(_lastUsedCofferItemId),
                            AcquiredAt = DateTime.Now,
                            InventoryType = firstSlot.Value.InventoryType,
                            SlotIndex = firstSlot.Value.SlotIndex
                        });
                    }

                    _consumptionRetryCount++;
                }
                else
                {
                    _logger.LogError("Maximum retry attempts reached for coffer: {CofferName}. Giving up.",
                        _cofferFunctions.GetCofferName(_lastUsedCofferItemId));
                    _consumptionRetryCount = 0;
                }

                _isVerifyingConsumption = false;
                _consumptionVerificationStartTime = DateTime.MinValue;
                _consumptionCheckCount = 0;
                _lastConsumptionCheck = DateTime.MinValue;
            }
            else
            {
                // Still waiting for consumption to be processed
                return;
            }
        }

        // Respect the delay between coffer uses
        if (DateTime.Now - _lastCofferUse < TimeSpan.FromMilliseconds(CofferUseDelayMs))
            return;

        // Don't attempt to open coffers while casting (additional safety check)
        if (_condition[ConditionFlag.Casting])
        {
            _logger.LogDebug("Currently casting, waiting before opening next coffer");
            return;
        }

        if (!_pendingCoffers.TryDequeue(out PendingCoffer? nextCoffer))
            return;

        // Verify the coffer still exists in inventory
        var currentCount = _inventoryMonitoringFunctions.GetCurrentItemCount(nextCoffer.ItemId);
        if (currentCount <= 0)
        {
            _logger.LogDebug("Coffer {CofferName} no longer in inventory, skipping", nextCoffer.Name);
            return;
        }

        // Check if inventory has space for potential rewards
        if (!HasSufficientInventorySpace())
        {
            _logger.LogInformation("Insufficient inventory space, delaying coffer opening for {CofferName}", nextCoffer.Name);

            // Put the coffer back in the queue and wait before retrying
            _pendingCoffers.Enqueue(nextCoffer);
            _lastCofferUse = DateTime.Now.AddMilliseconds(InventoryFullRetryDelayMs - CofferUseDelayMs);
            return;
        }

        // Generate a unique instance ID for this specific coffer attempt based on inventory slot
        uint cofferInstanceId;
        if (nextCoffer.SlotIndex >= 0)
        {
            // Use the pre-determined slot position
            cofferInstanceId = GenerateCofferInstanceIdFromSlot(nextCoffer.ItemId, nextCoffer.InventoryType, nextCoffer.SlotIndex);
        }
        else
        {
            // Find the first available slot for this item (for coffers added via CheckForNewCoffers)
            var firstSlot = FindFirstCofferSlot(nextCoffer.ItemId);
            if (firstSlot.HasValue)
            {
                cofferInstanceId = GenerateCofferInstanceIdFromSlot(nextCoffer.ItemId, firstSlot.Value.InventoryType, firstSlot.Value.SlotIndex);
            }
            else
            {
                // Fallback to legacy method if we can't find the coffer
                _logger.LogWarning("Could not find coffer {CofferName} in inventory for slot-based ID, using fallback", nextCoffer.Name);
                cofferInstanceId = GenerateCofferInstanceId(nextCoffer.ItemId, nextCoffer.AcquiredAt);
            }
        }

        // Check if we've already processed this specific coffer instance
        if (_processedCofferInstances.Contains(cofferInstanceId))
        {
            _logger.LogDebug("Coffer instance {CofferName} (ID: {InstanceId}) already processed, skipping", nextCoffer.Name, cofferInstanceId);
            return;
        }

        // Store pre-use inventory count for consumption verification
        _preUseCofferCount = _inventoryMonitoringFunctions.GetCurrentItemCount(nextCoffer.ItemId);
        _lastUsedCofferItemId = nextCoffer.ItemId;

        // Attempt to use the coffer
        _logger.LogInformation("Opening coffer: {CofferName} (Instance ID: {InstanceId}, Pre-use count: {PreCount})",
            nextCoffer.Name, cofferInstanceId, _preUseCofferCount);
        bool success = _gameFunctions.UseItem(nextCoffer.ItemId);

        if (success)
        {
            _logger.LogInformation("UseItem returned success for coffer: {CofferName} - waiting for cast completion and consumption verification", nextCoffer.Name);

            // Mark this coffer instance as processed (will be removed if consumption fails)
            _processedCofferInstances.Add(cofferInstanceId);

            // Set up casting wait state - consumption verification will start after casting completes
            _isWaitingForCasting = true;
            _castingStartedAt = DateTime.Now;
            _lastCofferUse = DateTime.Now;
        }
        else
        {
            _logger.LogWarning("UseItem failed for coffer: {CofferName}, re-queuing for retry", nextCoffer.Name);
            // Re-queue the coffer for another attempt, but limit retries to prevent infinite loops
            if (DateTime.Now - nextCoffer.AcquiredAt < TimeSpan.FromMinutes(5))
            {
                _pendingCoffers.Enqueue(nextCoffer);
            }
            _lastCofferUse = DateTime.Now;
        }
    }

    private bool IsSafeToOpenCoffers()
    {
        // Don't open coffers during combat
        if (_condition[ConditionFlag.InCombat])
        {
            return false;
        }

        // Don't open coffers during cutscenes or dialogue
        if (_condition[ConditionFlag.WatchingCutscene] ||
            _condition[ConditionFlag.WatchingCutscene78] ||
            _condition[ConditionFlag.OccupiedInEvent] ||
            _condition[ConditionFlag.OccupiedInCutSceneEvent])
        {
            return false;
        }

        // Don't open coffers while crafting or gathering
        if (_condition[ConditionFlag.Crafting] ||
            _condition[ConditionFlag.Gathering] ||
            _condition[ConditionFlag.PreparingToCraft])
        {
            return false;
        }

        // Don't open coffers while mounted (some coffers can't be opened while mounted)
        if (_condition[ConditionFlag.Mounted])
        {
            return false;
        }

        // Don't open coffers during teleporting or between areas
        if (_condition[ConditionFlag.BetweenAreas] ||
            _condition[ConditionFlag.BetweenAreas51])
        {
            return false;
        }

        // Don't open coffers while in duties unless specifically allowed
        if (_condition[ConditionFlag.BoundByDuty] ||
            _condition[ConditionFlag.BoundByDuty56] ||
            _condition[ConditionFlag.BoundByDuty95])
        {
            return false;
        }

        // Don't open coffers during long animations or critical casting
        if (_condition[ConditionFlag.Casting87])
        {
            return false;
        }

        // Don't open coffers during casting unless we're waiting for our own coffer casting to complete
        if (_condition[ConditionFlag.Casting] && !_isWaitingForCasting)
        {
            return false;
        }

        // Don't open coffers while diving or swimming
        if (_condition[ConditionFlag.Diving] ||
            _condition[ConditionFlag.Swimming])
        {
            return false;
        }

        return true;
    }

    private bool HasSufficientInventorySpace()
    {
        // This is a simplified check - in a more robust implementation,
        // we might want to check specific inventory types or calculate
        // exact space requirements based on the coffer's contents

        // For now, ensure we have at least a few free slots
        const int minimumFreeSlots = 3;

        // Count used slots in main inventory
        unsafe
        {
            var inventoryManager = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
            if (inventoryManager == null)
                return false;

            int usedSlots = 0;
            int totalSlots = 0;

            for (var inventoryType = FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory1;
                 inventoryType <= FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory4;
                 ++inventoryType)
            {
                var container = inventoryManager->GetInventoryContainer(inventoryType);
                if (container == null)
                    continue;

                totalSlots += container->Size;

                for (int i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot != null && slot->ItemId != 0)
                        usedSlots++;
                }
            }

            int freeSlots = totalSlots - usedSlots;
            return freeSlots >= minimumFreeSlots;
        }
    }

    /// <summary>
    /// Gets the number of pending coffers waiting to be opened.
    /// </summary>
    public int PendingCofferCount => _pendingCoffers.Count;

    /// <summary>
    /// Gets information about pending coffers for display purposes.
    /// </summary>
    public IEnumerable<string> GetPendingCofferNames()
    {
        return _pendingCoffers.Select(c => c.Name);
    }

    /// <summary>
    /// Generates a unique instance ID for a coffer based on its item ID and specific inventory slot.
    /// This ensures the same physical coffer gets the same ID across multiple processing runs.
    /// </summary>
    private static uint GenerateCofferInstanceIdFromSlot(uint itemId, FFXIVClientStructs.FFXIV.Client.Game.InventoryType inventoryType, int slotIndex)
    {
        // Create a unique ID based on inventory type, slot index, and item ID
        // This ensures the same physical coffer in the same slot gets the same ID
        uint inventoryTypeId = (uint)inventoryType;
        uint slotIndexUint = (uint)slotIndex;
        return (itemId << 16) ^ (inventoryTypeId << 8) ^ slotIndexUint;
    }

    /// <summary>
    /// Gets the inventory slots containing the specified coffer item.
    /// </summary>
    private List<(FFXIVClientStructs.FFXIV.Client.Game.InventoryType InventoryType, int SlotIndex)> GetCofferSlotsInInventory(uint itemId, int maxSlots)
    {
        var slots = new List<(FFXIVClientStructs.FFXIV.Client.Game.InventoryType, int)>();

        try
        {
            unsafe
            {
                var inventoryManager = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
                if (inventoryManager == null)
                    return slots;

                // Find all slots containing this item ID
                for (var inventoryType = FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory1;
                     inventoryType <= FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory4;
                     ++inventoryType)
                {
                    var container = inventoryManager->GetInventoryContainer(inventoryType);
                    if (container == null)
                        continue;

                    for (int i = 0; i < container->Size && slots.Count < maxSlots; i++)
                    {
                        var slot = container->GetInventorySlot(i);
                        if (slot != null && slot->ItemId == itemId)
                        {
                            slots.Add((inventoryType, i));
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get coffer slots for item {ItemId}", itemId);
        }

        return slots;
    }

    /// <summary>
    /// Finds the first inventory slot containing the specified coffer item.
    /// </summary>
    private (FFXIVClientStructs.FFXIV.Client.Game.InventoryType InventoryType, int SlotIndex)? FindFirstCofferSlot(uint itemId)
    {
        try
        {
            unsafe
            {
                var inventoryManager = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
                if (inventoryManager == null)
                    return null;

                // Find the first slot containing this item ID
                for (var inventoryType = FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory1;
                     inventoryType <= FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory4;
                     ++inventoryType)
                {
                    var container = inventoryManager->GetInventoryContainer(inventoryType);
                    if (container == null)
                        continue;

                    for (int i = 0; i < container->Size; i++)
                    {
                        var slot = container->GetInventorySlot(i);
                        if (slot != null && slot->ItemId == itemId)
                        {
                            return (inventoryType, i);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to find first coffer slot for item {ItemId}", itemId);
        }

        return null;
    }

    /// <summary>
    /// Legacy method for generating instance IDs based on acquisition time.
    /// Kept for compatibility but no longer used in main processing.
    /// </summary>
    private static uint GenerateCofferInstanceId(uint itemId, DateTime acquiredAt)
    {
        // Use a simple hash combination of item ID and acquisition time ticks
        // This should be unique enough for our purposes within a reasonable timeframe
        return (uint)(itemId.GetHashCode() ^ acquiredAt.Ticks.GetHashCode());
    }

    /// <summary>
    /// Cleans up old processed coffer instances to prevent memory buildup.
    /// Called periodically during normal operation.
    /// </summary>
    private void CleanupOldProcessedInstances()
    {
        // Only cleanup every 30 seconds to avoid excessive work
        if (DateTime.Now - _lastQuestCompletionTime < TimeSpan.FromSeconds(30))
            return;

        // If we have a lot of processed instances, clear them periodically
        // Since coffer instances are tied to acquisition time, old ones are no longer relevant
        if (_processedCofferInstances.Count > 100)
        {
            _logger.LogDebug("Cleaning up {Count} old processed coffer instances", _processedCofferInstances.Count);
            _processedCofferInstances.Clear();
        }
    }

    /// <summary>
    /// Processes all coffers currently in the inventory after quest completion.
    /// This is a simplified approach that scans the entire inventory for any coffers.
    /// Includes safety checks to prevent duplicate processing.
    /// </summary>
    public void ProcessAllCoffersInInventory()
    {
        if (!_configuration.General.AutoOpenCoffers)
        {
            _logger.LogDebug("Auto-open coffers is disabled in configuration");
            return;
        }

        // Safety check: prevent duplicate processing if already running
        if (IsProcessingInventoryCoffers)
        {
            _logger.LogInformation("CofferController is already processing inventory coffers - skipping duplicate call");
            return;
        }

        // Safety check: prevent processing if already running for quest completion
        if (_isRunning && _hasPendingCoffersFromQuest)
        {
            _logger.LogInformation("CofferController is already running for quest completion coffers - skipping duplicate call");
            return;
        }

        // Enhanced safety check: prevent duplicate processing within a short time window
        // This prevents multiple triggers from processing the same inventory state
        if (DateTime.Now - _lastInventoryProcessingStart < TimeSpan.FromSeconds(3))
        {
            _logger.LogInformation("Skipping coffer processing - recently started inventory processing at {LastTime}", _lastInventoryProcessingStart);
            return;
        }

        _logger.LogInformation("Processing all coffers in inventory");
        _lastInventoryProcessingStart = DateTime.Now;

        var allCoffers = GetAllCoffersInInventory();
        if (allCoffers.Count == 0)
        {
            _logger.LogDebug("No coffers found in inventory");
            IsProcessingInventoryCoffers = false;
            return;
        }

        // Check if this is a duplicate inventory state by comparing coffer counts
        if (IsDuplicateInventoryState(allCoffers))
        {
            _logger.LogInformation("Skipping coffer processing - inventory state unchanged since last processing");
            IsProcessingInventoryCoffers = false;
            return;
        }

        _logger.LogInformation("Found {CofferCount} coffer types in inventory to process", allCoffers.Count);

        // Update our known coffer counts for future duplicate detection
        UpdateKnownCofferCounts(allCoffers);

        // Clear existing pending coffers and add all found coffers
        _pendingCoffers.Clear();

        foreach (var (itemId, quantity) in allCoffers)
        {
            var cofferName = _cofferFunctions.GetCofferName(itemId);
            _logger.LogInformation("Adding coffer to processing queue: {CofferName} x{Quantity}", cofferName, quantity);

            // For each coffer instance, we need to track inventory slots to prevent duplicates
            var cofferSlots = GetCofferSlotsInInventory(itemId, quantity);
            for (int i = 0; i < quantity && i < cofferSlots.Count; i++)
            {
                _pendingCoffers.Enqueue(new PendingCoffer
                {
                    ItemId = itemId,
                    Name = cofferName,
                    AcquiredAt = DateTime.Now,
                    InventoryType = cofferSlots[i].InventoryType,
                    SlotIndex = cofferSlots[i].SlotIndex
                });
            }
        }

        // Set processing state and start the controller
        IsProcessingInventoryCoffers = true;
        _isRunning = true;
        _isWaitingForQuestCompletion = false;
        _hasPendingCoffersFromQuest = true;
        _lastCofferCheck = DateTime.MinValue;
        _lastCofferUse = DateTime.MinValue;
    }

    /// <summary>
    /// Gets all coffers currently in the inventory.
    /// </summary>
    /// <returns>Dictionary of coffer item ID to quantity</returns>
    private Dictionary<uint, int> GetAllCoffersInInventory()
    {
        var coffers = new Dictionary<uint, int>();

        try
        {
            unsafe
            {
                var inventoryManager = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
                if (inventoryManager == null)
                {
                    _logger.LogWarning("InventoryManager is null when scanning for coffers");
                    return coffers;
                }

                // Check main inventory containers (Inventory1-4)
                for (var inventoryType = FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory1;
                     inventoryType <= FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory4;
                     ++inventoryType)
                {
                    var container = inventoryManager->GetInventoryContainer(inventoryType);
                    if (container == null)
                        continue;

                    for (int i = 0; i < container->Size; i++)
                    {
                        var slot = container->GetInventorySlot(i);
                        if (slot == null || slot->ItemId == 0)
                            continue;

                        uint itemId = slot->ItemId;
                        int quantity = slot->Quantity;

                        if (_cofferFunctions.IsCoffer(itemId))
                        {
                            if (coffers.ContainsKey(itemId))
                                coffers[itemId] += quantity;
                            else
                                coffers[itemId] = quantity;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning inventory for coffers");
        }

        return coffers;
    }

    /// <summary>
    /// Checks if the current inventory state is a duplicate of what we recently processed.
    /// This prevents opening the same coffers multiple times when multiple triggers fire.
    /// </summary>
    private bool IsDuplicateInventoryState(Dictionary<uint, int> currentCoffers)
    {
        // If we have no previous state, this is not a duplicate
        if (_lastKnownCofferCounts.Count == 0)
            return false;

        // Check if the coffer counts are identical to our last known state
        if (currentCoffers.Count != _lastKnownCofferCounts.Count)
            return false;

        foreach (var (itemId, count) in currentCoffers)
        {
            if (!_lastKnownCofferCounts.TryGetValue(itemId, out int lastCount) || count != lastCount)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Updates our tracking of known coffer counts for duplicate detection.
    /// </summary>
    private void UpdateKnownCofferCounts(Dictionary<uint, int> currentCoffers)
    {
        _lastKnownCofferCounts.Clear();
        foreach (var (itemId, count) in currentCoffers)
        {
            _lastKnownCofferCounts[itemId] = count;
        }
    }

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;
        Stop("Dispose");
    }

    private sealed class PendingCoffer
    {
        public required uint ItemId { get; init; }
        public required string Name { get; init; }
        public required DateTime AcquiredAt { get; init; }
        public FFXIVClientStructs.FFXIV.Client.Game.InventoryType InventoryType { get; init; }
        public int SlotIndex { get; init; }
    }
}