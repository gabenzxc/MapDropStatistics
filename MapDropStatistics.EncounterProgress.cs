using System.Collections.Generic;
using System.Linq;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;

namespace MapDropStatistics;

public partial class MapDropStatistics
{
    private readonly Dictionary<(bool InMirage, uint Id), ObservedEncounter> _progressEncounters = [];
    private long _nextEncounterProgressRead;
    private long _nextDeliriumRewardRead;
    private bool _deliriumRewardStarted;

    private sealed class ObservedEncounter(ProgressEncounter kind, Entity entity)
    {
        public ProgressEncounter Kind { get; } = kind;
        public Entity Entity { get; set; } = entity;
        public bool Complete { get; set; }
    }

    private void ResetEncounterProgress()
    {
        _progressEncounters.Clear();
        _nextEncounterProgressRead = 0;
        _nextDeliriumRewardRead = 0;
        _deliriumRewardStarted = false;
    }

    private void ObserveProgressEncounter(Entity entity, string metadata)
    {
        var kind = EncounterProgressRules.Identify(metadata);
        if (kind == ProgressEncounter.None || entity.Id == 0)
            return;

        var key = (_inMirage, entity.Id);
        if (_progressEncounters.TryGetValue(key, out var observed) && observed.Kind == kind)
            observed.Entity = entity;
        else
            _progressEncounters[key] = observed = new ObservedEncounter(kind, entity);
        ReadEncounterState(observed);
    }

    private static void ReadEncounterState(ObservedEncounter observed)
    {
        var entity = observed.Entity;
        if (observed.Complete || !entity.IsValid || EncounterProgressRules.Identify(entity.Metadata) != observed.Kind)
            return;

        if (observed.Kind == ProgressEncounter.Abyss)
        {
            observed.Complete = EncounterProgressRules.IsComplete(observed.Kind, entity.GetComponent<Transitionable>()?.Flag1);
            return;
        }

        var states = entity.GetComponent<StateMachine>()?.States;
        if (states == null)
            return;
        var stateName = EncounterProgressRules.SharedState(observed.Kind);
        var state = states.FirstOrDefault(state => state.Name == stateName)?.Value;
        var shattered = observed.Kind == ProgressEncounter.Legion
            ? states.FirstOrDefault(state => state.Name == EncounterProgressRules.LegionShatteredState)?.Value
            : null;
        observed.Complete = EncounterProgressRules.IsComplete(observed.Kind, state, shattered);
    }

    private void UpdateEncounterProgress(long now)
    {
        if (now < _nextEncounterProgressRead)
            return;
        _nextEncounterProgressRead = now + 250;

        if (!_deliriumRewardStarted && now >= _nextDeliriumRewardRead)
        {
            _nextDeliriumRewardRead = now + 1000;
            var rewards = GameController.IngameState.ServerData?.DeliriumRewards;
            if (rewards?.Any(reward => reward.TargetProgress > 0 && !string.IsNullOrEmpty(reward.Type?.Id)) == true)
            {
                MarkEncounterZone("delirium");
                RecordEncounterSignal("Area state", "Delirium reward progress available");
            }
            _deliriumRewardStarted = rewards?.Any(reward => (reward.Count > 0 || reward.CurrentProgress > 0) &&
                !string.IsNullOrEmpty(reward.Type?.Id)) == true;
        }

        var legionSeen = 0;
        var legionClosed = 0;
        var abyssSeen = 0;
        var abyssClosed = 0;
        var breachSeen = 0;
        var breachClosed = 0;
        var mirageActivated = _inMirage || _activeRun.EncounterProgress?.MirageActivated == true ||
            _lastRunModifiers.GetValueOrDefault(GameStat.MapMirageChosenWishIndex1ForLogging)?.Value > 0;
        var deliriumStarted = _deliriumRewardStarted ||
            _lastRunModifiers.GetValueOrDefault(GameStat.MapHasAfflictionEverywhere)?.Value > 0;
        foreach (var ((inMirage, id), encounter) in _progressEncounters)
        {
            if (inMirage == _inMirage && !encounter.Complete)
            {
                var entity = EntityListWrapper.GetEntityById(id);
                if (entity?.IsValid == true && EncounterProgressRules.Identify(entity.Metadata) == encounter.Kind)
                    encounter.Entity = entity;
                ReadEncounterState(encounter);
            }
            switch (encounter.Kind)
            {
                case ProgressEncounter.Legion or ProgressEncounter.EndlessLegion:
                    legionSeen++;
                    if (encounter.Complete)
                        legionClosed++;
                    break;
                case ProgressEncounter.Abyss:
                    abyssSeen++;
                    if (encounter.Complete)
                        abyssClosed++;
                    break;
                case ProgressEncounter.UnstableBreach or ProgressEncounter.BreachHive:
                    breachSeen++;
                    if (encounter.Complete)
                        breachClosed++;
                    break;
                case ProgressEncounter.Mirage:
                    mirageActivated |= encounter.Complete;
                    break;
                case ProgressEncounter.Delirium:
                    deliriumStarted |= encounter.Complete;
                    break;
            }
        }

        if (legionSeen == 0 && abyssSeen == 0 && breachSeen == 0 && !mirageActivated && !deliriumStarted)
            return;

        var progress = new MapEncounterProgress(legionSeen, legionClosed, abyssSeen, abyssClosed, mirageActivated, deliriumStarted)
        {
            WildwoodEntered = _activeRun.EncounterProgress?.WildwoodEntered == true,
            BreachSeen = breachSeen,
            BreachClosed = breachClosed
        };
        if (progress != _activeRun.EncounterProgress)
        {
            _activeRun.EncounterProgress = progress;
            _nextRunSave = 0;
        }
    }
}
