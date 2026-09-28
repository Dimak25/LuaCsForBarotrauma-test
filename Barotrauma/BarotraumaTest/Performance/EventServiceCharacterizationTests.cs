extern alias Client;

using Client::Barotrauma;
using Client::Barotrauma.LuaCs;
using Client::Barotrauma.LuaCs.Events;
using FluentAssertions;
using MoonSharp.Interpreter;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace TestProject.Performance;

/// <summary>
/// Characterization tests for <see cref="EventService"/>: they pin down the behavior the rest of the game and
/// existing mods rely on, so that performance work on the event layer (PERFORMANCE_PLAN.md, stage 3) can be
/// checked against it.
///
/// A test whose name starts with "Quirk_" pins behavior that looks accidental. It is here so that a change is
/// deliberate: if you fix such a quirk on purpose, update the test in the same commit and say so in the PR.
///
/// Subscriber ORDER is intentionally never asserted: subscribers are kept in a ConcurrentDictionary keyed by
/// hash, so the order is unspecified today.
/// </summary>
public sealed class EventServiceCharacterizationTests
{
    private static EventServiceFactory.Created New() => EventServiceFactory.Create();

    private static int ErrorCount(RecordingProxy logger) => logger.Calls.Count(c => c.Method == nameof(ILoggerService.LogError));

    #region PublishEvent

    [Fact]
    public void PublishEvent_WithoutSubscribers_SucceedsAndDoesNotInvokeTheAction()
    {
        var (service, _, _) = New();
        int invoked = 0;

        var result = service.PublishEvent<ITestEvent>(_ => invoked++);

        result.IsSuccess.Should().BeTrue();
        invoked.Should().Be(0);
    }

    [Fact]
    public void PublishEvent_InvokesEverySubscriberOnceWithTheGivenArguments()
    {
        var (service, _, _) = New();
        var subscribers = Enumerable.Range(0, 5).Select(_ => new TestSubscriber()).ToArray();
        foreach (var s in subscribers) { service.Subscribe<ITestEvent>(s).IsSuccess.Should().BeTrue(); }

        var result = service.PublishEvent<ITestEvent>(x => x.OnTest(42));

        result.IsSuccess.Should().BeTrue();
        subscribers.Should().OnlyContain(s => s.CallCount == 1 && s.Received[0] == 42);
    }

    [Fact]
    public void PublishEvent_OnlyReachesSubscribersOfThatEventType()
    {
        var (service, _, _) = New();
        var test = new TestSubscriber();
        var other = new OtherSubscriber();
        service.Subscribe<ITestEvent>(test);
        service.Subscribe<IOtherTestEvent>(other);

        service.PublishEvent<ITestEvent>(x => x.OnTest(1));

        test.CallCount.Should().Be(1);
        other.CallCount.Should().Be(0);
    }

    [Fact]
    public void PublishEvent_ThrowingSubscriber_DoesNotStopTheOthers_ReturnsFailure_AndLogsTheError()
    {
        var (service, logger, _) = New();
        var thrower = new ThrowingSubscriber();
        var healthy = new TestSubscriber();
        service.Subscribe<ITestEvent>(thrower);
        service.Subscribe<ITestEvent>(healthy);

        var result = service.PublishEvent<ITestEvent>(x => x.OnTest(7));

        thrower.CallCount.Should().Be(1);
        healthy.Received.Should().Equal(7);
        result.IsFailed.Should().BeTrue();
        ErrorCount(logger).Should().Be(1);
    }

    [Fact]
    public void PublishEvent_ActionIsInvokedOncePerSubscriber_NotOncePerCall()
    {
        var (service, _, _) = New();
        service.Subscribe<ITestEvent>(new TestSubscriber());
        service.Subscribe<ITestEvent>(new TestSubscriber());
        int invoked = 0;

        service.PublishEvent<ITestEvent>(_ => invoked++);

        invoked.Should().Be(2);
    }

    #endregion

    #region Subscribe / Unsubscribe / Clear

    [Fact]
    public void Subscribe_SameInstanceTwice_Throws()
    {
        var (service, _, _) = New();
        var subscriber = new TestSubscriber();
        service.Subscribe<ITestEvent>(subscriber);

        Action second = () => service.Subscribe<ITestEvent>(subscriber);

        second.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Subscribe_Null_Throws()
    {
        var (service, _, _) = New();

        Action act = () => service.Subscribe<ITestEvent>((ITestEvent)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Unsubscribe_StopsDelivery_AndUnsubscribingUnknownInstanceIsHarmless()
    {
        var (service, _, _) = New();
        var subscriber = new TestSubscriber();
        service.Subscribe<ITestEvent>(subscriber);

        service.Unsubscribe<ITestEvent>(subscriber);
        service.Unsubscribe<ITestEvent>(new TestSubscriber());
        service.Unsubscribe<IOtherTestEvent>(new OtherSubscriber());
        service.PublishEvent<ITestEvent>(x => x.OnTest(1));

        subscriber.CallCount.Should().Be(0);
    }

    [Fact]
    public void Subscribe_AfterUnsubscribe_WorksAgain()
    {
        var (service, _, _) = New();
        var subscriber = new TestSubscriber();
        service.Subscribe<ITestEvent>(subscriber);
        service.Unsubscribe<ITestEvent>(subscriber);

        service.Subscribe<ITestEvent>(subscriber).IsSuccess.Should().BeTrue();
        service.PublishEvent<ITestEvent>(x => x.OnTest(3));

        subscriber.Received.Should().Equal(3);
    }

    [Fact]
    public void ClearAllEventSubscribers_OnlyRemovesTheGivenEventType()
    {
        var (service, _, _) = New();
        var test = new TestSubscriber();
        var other = new OtherSubscriber();
        service.Subscribe<ITestEvent>(test);
        service.Subscribe<IOtherTestEvent>(other);

        service.ClearAllEventSubscribers<ITestEvent>();
        service.PublishEvent<ITestEvent>(x => x.OnTest(1));
        service.PublishEvent<IOtherTestEvent>(x => x.OnOther());

        test.CallCount.Should().Be(0);
        other.CallCount.Should().Be(1);
    }

    [Fact]
    public void ClearAllSubscribers_RemovesEverything()
    {
        var (service, _, _) = New();
        var test = new TestSubscriber();
        var other = new OtherSubscriber();
        service.Subscribe<ITestEvent>(test);
        service.Subscribe<IOtherTestEvent>(other);

        service.ClearAllSubscribers();
        service.PublishEvent<ITestEvent>(x => x.OnTest(1));
        service.PublishEvent<IOtherTestEvent>(x => x.OnOther());

        (test.CallCount + other.CallCount).Should().Be(0);
    }

    [Fact]
    public void Subscribing_FromInsideAHandler_DoesNotThrow()
    {
        // Mods subscribe and unsubscribe from inside event handlers. Whatever the internal collection is,
        // that must stay legal.
        var (service, _, _) = New();
        var late = new TestSubscriber();
        var adder = new ActionSubscriber(() => service.Subscribe<ITestEvent>(late));
        service.Subscribe<ITestEvent>(adder);

        Action act = () => service.PublishEvent<ITestEvent>(x => x.OnTest(1));

        act.Should().NotThrow();
    }

    [Fact]
    public void Unsubscribing_FromInsideAHandler_DoesNotThrow()
    {
        var (service, _, _) = New();
        ActionSubscriber remover = null!;
        remover = new ActionSubscriber(() => service.Unsubscribe<ITestEvent>(remover));
        service.Subscribe<ITestEvent>(remover);

        Action act = () => service.PublishEvent<ITestEvent>(x => x.OnTest(1));

        act.Should().NotThrow();
    }

    private sealed class ActionSubscriber : ITestEvent
    {
        private readonly Action action;
        public ActionSubscriber(Action action) => this.action = action;
        public void OnTest(int value) => action();
    }

    #endregion

    #region Dispatcher services

    [Fact]
    public void Dispatcher_ReceivesEventsPublishedOnThePrimaryService()
    {
        var (primary, _, _) = New();
        var (secondary, _, _) = New();
        var onPrimary = new TestSubscriber();
        var onSecondary = new TestSubscriber();
        primary.Subscribe<ITestEvent>(onPrimary);
        secondary.Subscribe<ITestEvent>(onSecondary);
        primary.AddDispatcherEventService(secondary);

        primary.PublishEvent<ITestEvent>(x => x.OnTest(5));

        onPrimary.Received.Should().Equal(5);
        onSecondary.Received.Should().Equal(5);
    }

    [Fact]
    public void Dispatcher_StopsReceivingAfterItIsRemoved()
    {
        var (primary, _, _) = New();
        var (secondary, _, _) = New();
        primary.Subscribe<ITestEvent>(new TestSubscriber());
        var onSecondary = new TestSubscriber();
        secondary.Subscribe<ITestEvent>(onSecondary);
        primary.AddDispatcherEventService(secondary);
        primary.RemoveDispatcherEventService(secondary);

        primary.PublishEvent<ITestEvent>(x => x.OnTest(5));

        onSecondary.CallCount.Should().Be(0);
    }

    [Fact]
    public void Quirk_Dispatcher_IsNotNotified_WhenThePrimaryServiceHasNoSubscribersForThatEvent()
    {
        // PublishEvent returns early when the primary service has no subscribers for the event type,
        // before it forwards to the dispatcher services. Looks accidental; pinned so a change is deliberate.
        var (primary, _, _) = New();
        var (secondary, _, _) = New();
        var onSecondary = new TestSubscriber();
        secondary.Subscribe<ITestEvent>(onSecondary);
        primary.AddDispatcherEventService(secondary);

        primary.PublishEvent<ITestEvent>(x => x.OnTest(5));

        onSecondary.CallCount.Should().Be(0);
    }

    #endregion

    #region Lua-facing API (aliases, legacy string events)

    [Fact]
    public void LuaAlias_ThinkEvent_CallsTheLuaFunctionWithTheDeltaTime()
    {
        var (service, _, _) = New();
        service.RegisterLuaEventAlias<IEventUpdate>("think", nameof(IEventUpdate.OnUpdate)).IsSuccess.Should().BeTrue();
        object?[]? received = null;
        service.Add("think", "test-id", args => { received = args; return null!; });

        service.PublishEvent<IEventUpdate>(x => x.OnUpdate(0.5));

        received.Should().NotBeNull();
        received!.Should().Equal(new object[] { 0.5 });
    }

    [Fact]
    public void LuaAlias_RemoveByIdentifier_StopsDelivery()
    {
        var (service, _, _) = New();
        service.RegisterLuaEventAlias<IEventUpdate>("think", nameof(IEventUpdate.OnUpdate));
        int calls = 0;
        service.Add("think", "id", _ => { calls++; return null!; });
        service.PublishEvent<IEventUpdate>(x => x.OnUpdate(1));

        service.Remove("think", "id");
        service.PublishEvent<IEventUpdate>(x => x.OnUpdate(1));

        calls.Should().Be(1);
    }

    [Fact]
    public void LuaAlias_AddingTheSameIdentifierTwice_ReplacesTheFirstSubscriber()
    {
        var (service, _, _) = New();
        service.RegisterLuaEventAlias<IEventUpdate>("think", nameof(IEventUpdate.OnUpdate));
        int first = 0, second = 0;
        service.Add("think", "same", _ => { first++; return null!; });
        service.Add("think", "same", _ => { second++; return null!; });

        service.PublishEvent<IEventUpdate>(x => x.OnUpdate(1));

        (first, second).Should().Be((0, 1));
    }

    [Fact]
    public void SubscribeWithCallbackDictionary_UsesTheEventsLuaRunner()
    {
        var (service, _, _) = New();
        double? received = null;
        service.Subscribe<IEventUpdate>("id", new Dictionary<string, LuaCsFunc>
        {
            [nameof(IEventUpdate.OnUpdate)] = args => { received = (double)args[0]; return null!; }
        });

        service.PublishEvent<IEventUpdate>(x => x.OnUpdate(0.25));

        received.Should().Be(0.25);
    }

    [Fact]
    public void LegacyEvent_Call_ReturnsTheSubscribersResult()
    {
        var (service, _, _) = New();
        service.Add("my.event", "id", args => $"got {args[0]}");

        service.Call<string>("my.event", 12).Should().Be("got 12");
    }

    [Fact]
    public void LegacyEvent_Call_WithoutSubscribersOrUnknownEvent_ReturnsDefault()
    {
        var (service, _, _) = New();
        service.Add("my.event", "id", _ => "x");
        service.Remove("my.event", "id");

        service.Call<string>("my.event").Should().BeNull();
        service.Call<string>("never.registered").Should().BeNull();
        service.Call<int>("never.registered").Should().Be(0);
    }

    [Fact]
    public void LegacyEvent_NamesAreCaseInsensitive()
    {
        var (service, _, _) = New();
        service.Add("Signal.Received", "id", _ => "hit");

        service.Call<string>("signal.received").Should().Be("hit");
        service.Call<string>("SIGNAL.RECEIVED").Should().Be("hit");
    }

    [Fact]
    public void LegacyEvent_Remove_StopsDelivery()
    {
        var (service, _, _) = New();
        service.Add("my.event", "id", _ => "x");

        service.Remove("my.event", "id");

        service.Call<string>("my.event").Should().BeNull();
    }

    [Fact]
    public void LegacyEvent_SameIdentifierReplacesTheSubscriber()
    {
        var (service, _, _) = New();
        service.Add("my.event", "id", _ => "first");
        service.Add("my.event", "id", _ => "second");

        service.Call<string>("my.event").Should().Be("second");
    }

    [Fact]
    public void LegacyEvent_LuaValuesAreConverted_AndNilIsIgnored()
    {
        var (service, _, _) = New();
        service.Add("string.event", "id", _ => DynValue.NewString("lua string"));
        service.Add("nil.event", "id", _ => DynValue.Nil);

        service.Call<string>("string.event").Should().Be("lua string");
        service.Call<string>("nil.event").Should().BeNull();
    }

    [Fact]
    public void Quirk_LegacyEvent_TupleResult_IsConvertedAsAWhole_NotByItsFirstValue()
    {
        // A Lua function returning several values (`return "first", true`) reaches Call<T> as one tuple, and the
        // whole tuple is converted: T=object yields a DynValue[], and T=string / T=double yield the default.
        // The "first value wins" reading of the code (`Tuple[0]`) is NOT what happens today.
        var (service, _, _) = New();
        service.Add("tuple.event", "id", _ => DynValue.NewTuple(DynValue.NewString("first"), DynValue.NewBoolean(true)));

        service.Call<string>("tuple.event").Should().BeNull();
        service.Call<double>("tuple.event").Should().Be(0);
        var asObject = service.Call("tuple.event");
        asObject.Should().BeOfType<DynValue[]>();
        ((DynValue[])asObject).Select(v => v.ToObject()).Should().Equal("first", true);
    }

    [Fact]
    public void LegacyEvent_ClrValuesAreReturnedAsIs()
    {
        var (service, _, _) = New();
        service.Add("bool.event", "id", _ => true);

        service.Call<bool>("bool.event").Should().BeTrue();
    }

#if !DEBUG
    // In Debug builds EventService rethrows subscriber exceptions ("#if DEBUG throw;"), so this only holds in Release.
    [Fact]
    public void LegacyEvent_ThrowingSubscriber_IsLoggedAndTheOthersStillRun()
    {
        var (service, logger, _) = New();
        service.Add("my.event", "bad", _ => throw new InvalidOperationException("boom"));
        int healthyCalls = 0;
        service.Add("my.event", "good", _ => { healthyCalls++; return null!; });

        Action act = () => service.Call("my.event");

        act.Should().NotThrow();
        healthyCalls.Should().Be(1);
        ErrorCount(logger).Should().Be(1);
    }

    [Fact]
    public void LuaAlias_DuplicateRegistration_FailsWithoutThrowing()
    {
        var (service, _, _) = New();
        service.RegisterLuaEventAlias<IEventUpdate>("think", nameof(IEventUpdate.OnUpdate));

        var result = service.RegisterLuaEventAlias<IEventUpdate>("think", nameof(IEventUpdate.OnUpdate));

        result.IsFailed.Should().BeTrue();
    }
#endif

    [Fact]
    public void Quirk_UnsubscribeByEventName_DoesNotFindSubscribersRegisteredByType()
    {
        // Unsubscribe(string, string) looks the event up with a string-based key, while Subscribe<T>() stores a
        // type-based key; the keys don't compare equal, so this silently does nothing today.
        var (service, _, _) = New();
        int calls = 0;
        service.Subscribe<IEventUpdate>("id", new Dictionary<string, LuaCsFunc>
        {
            [nameof(IEventUpdate.OnUpdate)] = _ => { calls++; return null!; }
        });

        service.Unsubscribe(nameof(IEventUpdate), "id");
        service.PublishEvent<IEventUpdate>(x => x.OnUpdate(1));

        calls.Should().Be(1);
    }

    #endregion

    #region Lifecycle

    [Fact]
    public void Reset_ClearsAllSubscribers_AndTheServiceStaysUsable()
    {
        var (service, _, _) = New();
        var subscriber = new TestSubscriber();
        service.Subscribe<ITestEvent>(subscriber);
        service.Add("my.event", "id", _ => "x");

        service.Reset();
        service.PublishEvent<ITestEvent>(x => x.OnTest(1));

        subscriber.CallCount.Should().Be(0);
        service.Call<string>("my.event").Should().BeNull();
        service.Subscribe<ITestEvent>(subscriber).IsSuccess.Should().BeTrue();
        service.PublishEvent<ITestEvent>(x => x.OnTest(2));
        subscriber.Received.Should().Equal(2);
    }

    [Fact]
    public void Reset_ResetsTheLuaPatcher()
    {
        var (service, _, patcher) = New();

        service.Reset();

        patcher.Calls.Should().Contain(c => c.Method == nameof(ILuaPatcher.Reset));
    }

    [Fact]
    public void Dispose_MarksTheServiceDisposed_AndFurtherUseThrows()
    {
        var (service, _, patcher) = New();

        service.Dispose();

        service.IsDisposed.Should().BeTrue();
        patcher.Calls.Should().Contain(c => c.Method == nameof(IDisposable.Dispose));
        ((Action)(() => service.PublishEvent<ITestEvent>(_ => { }))).Should().Throw<ObjectDisposedException>();
        ((Action)(() => service.Subscribe<ITestEvent>(new TestSubscriber()))).Should().Throw<ObjectDisposedException>();
        ((Action)(() => service.Call("my.event"))).Should().Throw<ObjectDisposedException>();
    }

    #endregion
}
