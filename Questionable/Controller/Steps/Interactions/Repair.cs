using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using LLib.GameUI;
using Microsoft.Extensions.Logging;
using Questionable.Controller.Steps.Common;
using Questionable.Functions;
using Questionable.Model;
using Questionable.Model.Questing;

using static Questionable.Configuration;

namespace Questionable.Controller.Steps.Interactions;

internal static class Repair
{
    internal sealed record Task(ERepairMethod Method) : ITask
    {
        public override string ToString() => $"Repair({Method})";
    }

    internal sealed class Factory : ITaskFactory
    {
        private readonly GearFunctions _gearFunctions;
        private readonly Configuration _configuration;

        public Factory(GearFunctions gearFunctions, Configuration configuration)
        {
            _gearFunctions = gearFunctions;
            _configuration = configuration;
        }

        public IEnumerable<ITask> CreateAllTasks(Quest quest, QuestSequence sequence, QuestStep step)
        {
            // This factory is primarily used by the quest controller to inject repair tasks
            return [];
        }

        public ITask CreateRepairTask()
        {
            var repairConfig = _configuration.Repairs;
            return new Task(repairConfig.RepairMethod);
        }
    }

    internal sealed class Executor : TaskExecutor<Task>
    {
        private readonly ILogger<Executor> _logger;
        private readonly GearFunctions _gearFunctions;
        private readonly GameFunctions _gameFunctions;
        private readonly ICondition _condition;
        private readonly IGameGui _gameGui;

        private DateTime _interactionStarted = DateTime.MinValue;

        public Executor(
            ILogger<Executor> logger,
            GearFunctions gearFunctions,
            GameFunctions gameFunctions,
            ICondition condition,
            IGameGui gameGui)
        {
            _logger = logger;
            _gearFunctions = gearFunctions;
            _gameFunctions = gameFunctions;
            _condition = condition;
            _gameGui = gameGui;
        }

        protected override bool Start()
        {
            _logger.LogInformation("Starting repair task with method: {Method}", Task.Method);
            _interactionStarted = DateTime.Now;
            return true;
        }

        public override ETaskResult Update()
        {
            if (DateTime.Now.Subtract(_interactionStarted).TotalSeconds > 60)
            {
                _logger.LogWarning("Repair task timed out after 60 seconds");
                return ETaskResult.TaskComplete;
            }

            // Check if repair is no longer needed
            if (!_gearFunctions.NeedsRepair(0)) // Check if any gear still needs repair
            {
                _logger.LogInformation("Repair completed successfully");
                return ETaskResult.TaskComplete;
            }

            return Task.Method switch
            {
                ERepairMethod.SelfRepair => ExecuteSelfRepair(),
                ERepairMethod.RepairNpc => ExecuteNpcRepair(),
                _ => ETaskResult.TaskComplete
            };
        }

        private unsafe ETaskResult ExecuteSelfRepair()
        {
            if (!_gearFunctions.HasDarkMatter())
            {
                _logger.LogWarning("Cannot self-repair: no Dark Matter found in inventory");
                return ETaskResult.TaskComplete;
            }

            try
            {
                // Try to open the repair interface
                var repairKitId = GetBestDarkMatterId();
                if (repairKitId == 0)
                {
                    _logger.LogWarning("No suitable Dark Matter found");
                    return ETaskResult.TaskComplete;
                }

                // Use the repair kit
                var actionManager = ActionManager.Instance();
                if (actionManager != null)
                {
                    var generalAction = actionManager->GetActionStatus(ActionType.GeneralAction, 6); // Repair action
                    if (generalAction == 0) // 0 means action is available
                    {
                        _logger.LogDebug("Executing self-repair");
                        actionManager->UseAction(ActionType.GeneralAction, 6);
                        return ETaskResult.StillRunning;
                    }
                }

                return ETaskResult.StillRunning;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to execute self-repair");
                return ETaskResult.TaskComplete;
            }
        }

        private ETaskResult ExecuteNpcRepair()
        {
            _logger.LogInformation("NPC repair not yet implemented - falling back to self-repair if possible");

            if (_gearFunctions.HasDarkMatter())
                return ExecuteSelfRepair();

            _logger.LogWarning("Cannot repair: no Dark Matter available and NPC repair not implemented");
            return ETaskResult.TaskComplete;
        }

        private unsafe uint GetBestDarkMatterId()
        {
            var inventoryManager = InventoryManager.Instance();
            if (inventoryManager == null)
                return 0;

            // Priority order: Grade 8, 7, 6, 5, 4, 3 Dark Matter
            uint[] darkMatterIds = [33916, 10386, 5594, 5593, 5592, 5591];

            var containers = new[]
            {
                InventoryType.Inventory1,
                InventoryType.Inventory2,
                InventoryType.Inventory3,
                InventoryType.Inventory4
            };

            foreach (var darkMatterId in darkMatterIds)
            {
                foreach (var containerType in containers)
                {
                    var container = inventoryManager->GetInventoryContainer(containerType);
                    if (container == null) continue;

                    for (int i = 0; i < container->Size; ++i)
                    {
                        var item = container->GetInventorySlot(i);
                        if (item != null && item->ItemId == darkMatterId && item->Quantity > 0)
                            return darkMatterId;
                    }
                }
            }

            return 0;
        }

        public override bool ShouldInterruptOnDamage() => false;

        public override string ToString() => "RepairExecutor";
    }
}