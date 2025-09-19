using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Microsoft.Extensions.Logging;

namespace Questionable.Functions;

internal sealed class CofferFunctions
{
    private readonly IDataManager _dataManager;
    private readonly ILogger<CofferFunctions> _logger;

    private readonly HashSet<uint> _knownCofferItemIds = new();
    private readonly HashSet<string> _cofferNamePatterns = new()
    {
        "Coffer",
        "Chest",
        "Box",
        "Case",
        "Kit",
        "Bag",
        "Satchel",
        "Package",
        "Container"
    };

    public CofferFunctions(IDataManager dataManager, ILogger<CofferFunctions> logger)
    {
        _dataManager = dataManager;
        _logger = logger;
        InitializeCofferDatabase();
    }

    private void InitializeCofferDatabase()
    {
        _logger.LogDebug("Initializing coffer database...");

        var itemSheet = _dataManager.GetExcelSheet<Item>();
        if (itemSheet == null)
        {
            _logger.LogWarning("Could not load Item sheet for coffer identification");
            return;
        }

        var cofferCount = 0;
        foreach (var item in itemSheet)
        {
            if (IsItemCoffer(item))
            {
                _knownCofferItemIds.Add(item.RowId);
                cofferCount++;
            }
        }

        _logger.LogInformation("Loaded {CofferCount} known coffer items", cofferCount);
    }

    private bool IsItemCoffer(Item item)
    {
        if (item.RowId == 0 || string.IsNullOrEmpty(item.Name.ToString()))
            return false;

        var itemName = item.Name.ToString();
        var itemDescription = item.Description.ToString();

        // Check if item name contains coffer-like patterns
        if (_cofferNamePatterns.Any(pattern => itemName.Contains(pattern, System.StringComparison.OrdinalIgnoreCase)))
            return true;

        // Check item use category - coffers typically have ItemUICategory of 63 (Other)
        // and can be used from inventory
        if (item.ItemUICategory.RowId == 63 && item.ItemAction.RowId != 0)
        {
            // Additional check: coffers usually have descriptions mentioning opening or containing items
            if (itemDescription.Contains("open", System.StringComparison.OrdinalIgnoreCase) ||
                itemDescription.Contains("contain", System.StringComparison.OrdinalIgnoreCase) ||
                itemDescription.Contains("receive", System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Check for specific item action types that indicate opening behavior
        if (item.ItemAction.ValueNullable?.Type == 5)
        {
            // ItemAction type 5 is typically for containers/coffers
            return true;
        }

        return false;
    }

    public bool IsCoffer(uint itemId)
    {
        if (_knownCofferItemIds.Contains(itemId))
            return true;

        // Fallback: check the item dynamically if not in our cache
        var item = _dataManager.GetExcelSheet<Item>()?.GetRowOrDefault(itemId);
        if (item.HasValue && IsItemCoffer(item.Value))
        {
            _knownCofferItemIds.Add(itemId);
            _logger.LogDebug("Dynamically identified coffer: {ItemName} (ID: {ItemId})", item.Value.Name.ToString(), itemId);
            return true;
        }

        return false;
    }

    public string GetCofferName(uint itemId)
    {
        var item = _dataManager.GetExcelSheet<Item>()?.GetRowOrDefault(itemId);
        return item?.Name.ToString() ?? $"Unknown Coffer ({itemId})";
    }

    public IReadOnlySet<uint> GetKnownCofferIds() => _knownCofferItemIds;
}