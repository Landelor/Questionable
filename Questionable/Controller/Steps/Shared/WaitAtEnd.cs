using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using Questionable.Controller;
using Questionable.Controller.Steps.Common;
using Questionable.Controller.Utils;
using Questionable.Data;
using Questionable.External;
using Questionable.Functions;
using Questionable.Model;
using Questionable.Model.Questing;

namespace Questionable.Controller.Steps.Shared;

internal static class WaitAtEnd
{
    internal sealed class Factory(
        IClientState clientState,
        ICondition condition,
        TerritoryData territoryData,
        AutoDutyIpc autoDutyIpc,
        BossModIpc bossModIpc)
        : ITaskFactory
    {
        public IEnumerable<ITask> CreateAllTasks(Quest quest, QuestSequence sequence, QuestStep step)
        {
            if (step.CompletionQuestVariablesFlags.Count == 6 &&
                QuestWorkUtils.HasCompletionFlags(step.CompletionQuestVariablesFlags))
            {
                var task = new WaitForCompletionFlags((QuestId)quest.Id, step);
                var delay = new WaitDelay();
                return [task, delay, Next(quest, sequence)];
            }

            switch (step.InteractionType)
            {
                case EInteractionType.Combat:
                    if (step.EnemySpawnType == EEnemySpawnType.FinishCombatIfAny)
                        return [Next(quest, sequence)];

                    var notInCombat =
                        new WaitCondition.Task(() => !condition[ConditionFlag.InCombat], "Wait(not in combat)");
                    return
                    [
                        new WaitDelay(),
                        notInCombat,
                        new WaitDelay(),
                        Next(quest, sequence)
                    ];

                case EInteractionType.WaitForManualProgress:
                case EInteractionType.Instruction:
                case EInteractionType.Snipe:
                    return [new WaitNextStepOrSequence()];

                case EInteractionType.Duty when !autoDutyIpc.IsConfiguredToRunContent(step.DutyOptions):
                case EInteractionType.SinglePlayerDuty when !bossModIpc.IsConfiguredToRunSoloInstance(quest.Id, step.SinglePlayerDutyOptions):
                    return [new EndAutomation()];

                case EInteractionType.WalkTo:
                case EInteractionType.Jump:
                    // no need to wait if we're just moving around
                    return [Next(quest, sequence)];

                case EInteractionType.WaitForObjectAtPosition:
                    ArgumentNullException.ThrowIfNull(step.DataId);
                    ArgumentNullException.ThrowIfNull(step.Position);

                    return
                    [
                        new WaitObjectAtPosition(step.DataId.Value, step.Position.Value, step.NpcWaitDistance ?? 0.5f),
                        new WaitDelay(),
                        Next(quest, sequence)
                    ];

                case EInteractionType.Interact when step.TargetTerritoryId != null:
                case EInteractionType.UseItem when step.TargetTerritoryId != null:
                    ITask waitInteraction;
                    if (step.TerritoryId != step.TargetTerritoryId)
                    {
                        // interaction moves to a different territory
                        waitInteraction = new WaitCondition.Task(
                            () => clientState.TerritoryType == step.TargetTerritoryId,
                            $"Wait(tp to territory: {territoryData.GetNameAndId(step.TargetTerritoryId.Value)})");
                    }
                    else
                    {
                        Vector3 lastPosition = step.Position ?? clientState.LocalPlayer?.Position ?? Vector3.Zero;
                        waitInteraction = new WaitCondition.Task(() =>
                            {
                                Vector3? currentPosition = clientState.LocalPlayer?.Position;
                                if (currentPosition == null)
                                    return false;

                                // interaction moved to elsewhere in the zone
                                // the 'closest' locations are probably
                                //   - waking sands' solar
                                //   - rising stones' solar + dawn's respite
                                return (lastPosition - currentPosition.Value).Length() > 2;
                            }, $"Wait(tp away from {lastPosition.ToString("G", CultureInfo.InvariantCulture)})");
                    }

                    return
                    [
                        waitInteraction,
                        new WaitDelay(),
                        Next(quest, sequence)
                    ];

                case EInteractionType.AcceptQuest:
                {
                    var accept = new WaitQuestAccepted(step.PickUpQuestId ?? quest.Id);
                    var delay = new WaitDelay();
                    if (step.PickUpQuestId != null)
                        return [accept, delay, Next(quest, sequence)];
                    else
                        return [accept, delay];
                }

                case EInteractionType.CompleteQuest:
                {
                    var complete = new WaitQuestCompleted(step.TurnInQuestId ?? quest.Id);
                    var delay = new WaitDelay();
                    var waitCoffer = new WaitCofferProcessing(step.TurnInQuestId ?? quest.Id);
                    if (step.TurnInQuestId != null)
                        return [complete, delay, waitCoffer, Next(quest, sequence)];
                    else
                        return [complete, delay, waitCoffer];
                }

                case EInteractionType.Interact:
                default:
                    return [new WaitDelay(), Next(quest, sequence)];
            }
        }

        private static NextStep Next(Quest quest, QuestSequence sequence)
        {
            return new NextStep(quest.Id, sequence.Sequence);
        }
    }

    internal sealed record WaitDelay(TimeSpan Delay) : ITask
    {
        public WaitDelay()
            : this(TimeSpan.FromSeconds(1))
        {
        }

        public bool ShouldRedoOnInterrupt() => true;

        public override string ToString() => $"Wait(seconds: {Delay.TotalSeconds})";
    }

    internal sealed class WaitDelayExecutor : AbstractDelayedTaskExecutor<WaitDelay>
    {
        protected override bool StartInternal()
        {
            Delay = Task.Delay;
            return true;
        }

        public override bool ShouldInterruptOnDamage() => false;
    }

    internal sealed class WaitNextStepOrSequence : ITask
    {
        public override string ToString() => "Wait(next step or sequence)";
    }

    internal sealed class WaitNextStepOrSequenceExecutor : TaskExecutor<WaitNextStepOrSequence>
    {
        protected override bool Start() => true;

        public override ETaskResult Update() => ETaskResult.StillRunning;

        public override bool ShouldInterruptOnDamage() => false;
    }

    internal sealed record WaitForCompletionFlags(QuestId Quest, QuestStep Step) : ITask
    {
        public override string ToString() =>
            $"Wait(QW: {string.Join(", ", Step.CompletionQuestVariablesFlags.Select(x => x?.ToString() ?? "-"))})";
    }

    internal sealed class WaitForCompletionFlagsExecutor(QuestFunctions questFunctions)
        : TaskExecutor<WaitForCompletionFlags>
    {
        protected override bool Start() => true;

        public override ETaskResult Update()
        {
            QuestProgressInfo? questWork = questFunctions.GetQuestProgressInfo(Task.Quest);
            return questWork != null &&
                   QuestWorkUtils.MatchesQuestWork(Task.Step.CompletionQuestVariablesFlags, questWork)
                ? ETaskResult.TaskComplete
                : ETaskResult.StillRunning;
        }

        public override bool ShouldInterruptOnDamage() => false;
    }

    internal sealed record WaitObjectAtPosition(
        uint DataId,
        Vector3 Destination,
        float Distance) : ITask
    {
        public override string ToString() =>
            $"WaitObj({DataId} at {Destination.ToString("G", CultureInfo.InvariantCulture)} < {Distance})";
    }

    internal sealed class WaitObjectAtPositionExecutor(GameFunctions gameFunctions) : TaskExecutor<WaitObjectAtPosition>
    {
        protected override bool Start() => true;

        public override ETaskResult Update() =>
            gameFunctions.IsObjectAtPosition(Task.DataId, Task.Destination, Task.Distance)
                ? ETaskResult.TaskComplete
                : ETaskResult.StillRunning;

        public override bool ShouldInterruptOnDamage() => false;
    }

    internal sealed record WaitQuestAccepted(ElementId ElementId) : ITask
    {
        public override string ToString() => $"WaitQuestAccepted({ElementId})";
    }

    internal sealed class WaitQuestAcceptedExecutor(QuestFunctions questFunctions) : TaskExecutor<WaitQuestAccepted>
    {
        protected override bool Start() => true;

        public override ETaskResult Update()
        {
            return questFunctions.IsQuestAccepted(Task.ElementId)
                ? ETaskResult.TaskComplete
                : ETaskResult.StillRunning;
        }

        public override bool ShouldInterruptOnDamage() => false;
    }

    internal sealed record WaitQuestCompleted(ElementId ElementId) : ITask
    {
        public override string ToString() => $"WaitQuestComplete({ElementId})";
    }

    internal sealed class WaitQuestCompletedExecutor(QuestFunctions questFunctions) : TaskExecutor<WaitQuestCompleted>
    {
        protected override bool Start() => true;

        public override ETaskResult Update()
        {
            return questFunctions.IsQuestComplete(Task.ElementId) ? ETaskResult.TaskComplete : ETaskResult.StillRunning;
        }

        public override bool ShouldInterruptOnDamage() => false;
    }

    internal sealed record WaitCofferProcessing(ElementId QuestId) : ITask
    {
        public override string ToString() => $"WaitCofferProcessing({QuestId})";
    }

    internal sealed record NextStep(ElementId ElementId, int Sequence) : ILastTask
    {
        public override string ToString() => "NextStep";
    }

    internal sealed class WaitCofferProcessingExecutor(
        Configuration configuration,
        CofferController cofferController,
        ILogger<WaitCofferProcessingExecutor> logger) : TaskExecutor<WaitCofferProcessing>
    {
        private bool _cofferProcessingStarted;
        private bool _initialLoggedInUpdate;

        protected override bool Start()
        {
            logger.LogInformation("WaitCofferProcessingExecutor.Start() called for quest {QuestId}", Task.QuestId);
            logger.LogDebug("AutoOpenCoffers configuration is {Enabled}",
                configuration.General.AutoOpenCoffers ? "enabled" : "disabled");

            // Only start coffer processing if auto-open coffers is enabled
            if (!configuration.General.AutoOpenCoffers)
            {
                logger.LogInformation("Skipping coffer processing - AutoOpenCoffers is disabled");
                _cofferProcessingStarted = false;
                return true; // Skip processing, complete immediately
            }

            // Signal CofferController to start processing coffers for this quest
            logger.LogInformation("Starting coffer processing for quest {QuestId}", Task.QuestId);
            cofferController.OnQuestCompleted(Task.QuestId);
            _cofferProcessingStarted = true;
            _initialLoggedInUpdate = false;
            return true;
        }

        public override ETaskResult Update()
        {
            // Log initial state in first Update() call
            if (!_initialLoggedInUpdate)
            {
                logger.LogDebug("WaitCofferProcessingExecutor.Update() - Initial state: AutoOpenCoffers={Enabled}, Started={Started}, IsRunning={IsRunning}, HasPendingFromQuest={HasPending}, PendingCount={PendingCount}",
                    configuration.General.AutoOpenCoffers,
                    _cofferProcessingStarted,
                    cofferController.IsRunning,
                    cofferController.HasPendingCoffersFromQuest,
                    cofferController.PendingCofferCount);
                _initialLoggedInUpdate = true;
            }

            // If auto-open coffers is disabled, complete immediately
            if (!configuration.General.AutoOpenCoffers || !_cofferProcessingStarted)
            {
                logger.LogDebug("Completing immediately - AutoOpenCoffers disabled or processing not started");
                return ETaskResult.TaskComplete;
            }

            // Check if CofferController is still running
            if (cofferController.IsRunning)
            {
                logger.LogTrace("CofferController is still running, waiting... (PendingCount: {PendingCount})",
                    cofferController.PendingCofferCount);
                return ETaskResult.StillRunning;
            }

            // If no coffers were acquired from this quest, complete immediately
            if (!cofferController.HasPendingCoffersFromQuest && cofferController.PendingCofferCount == 0)
            {
                logger.LogInformation("No coffers were acquired from quest {QuestId}, completing task", Task.QuestId);
                return ETaskResult.TaskComplete;
            }

            // CofferController has finished processing all coffers
            logger.LogInformation("CofferController finished processing all coffers for quest {QuestId}", Task.QuestId);
            return ETaskResult.TaskComplete;
        }

        public override bool ShouldInterruptOnDamage() => false;
    }

    internal sealed class NextStepExecutor : TaskExecutor<NextStep>
    {
        protected override bool Start() => true;

        public override ETaskResult Update() => ETaskResult.NextStep;

        public override bool ShouldInterruptOnDamage() => false;
    }

    internal sealed class EndAutomation : ILastTask
    {
        public ElementId ElementId => throw new InvalidOperationException();
        public int Sequence => throw new InvalidOperationException();

        public override string ToString() => "EndAutomation";
    }
    internal sealed class EndAutomationExecutor : TaskExecutor<EndAutomation>
    {

        protected override bool Start() => true;

        public override ETaskResult Update() => ETaskResult.End;

        public override bool ShouldInterruptOnDamage() => false;
    }
}
