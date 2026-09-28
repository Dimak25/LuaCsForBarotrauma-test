extern alias Client;

using Client::Barotrauma.LuaCs;
using Client::Barotrauma.LuaCs.Events;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace TestProject.Performance;

/// <summary>
/// A test event type used to exercise <see cref="EventService"/> without depending on any game state.
/// </summary>
public interface ITestEvent : IEvent<ITestEvent>
{
    void OnTest(int value);
}

/// <summary>
/// A second, unrelated event type (used to verify that operations on one event type don't affect another).
/// </summary>
public interface IOtherTestEvent : IEvent<IOtherTestEvent>
{
    void OnOther();
}

public sealed class TestSubscriber : ITestEvent
{
    public readonly List<int> Received = new();
    public int CallCount => Received.Count;
    public void OnTest(int value) => Received.Add(value);
}

public sealed class ThrowingSubscriber : ITestEvent
{
    public int CallCount;
    public void OnTest(int value)
    {
        CallCount++;
        throw new InvalidOperationException("thrown by test subscriber");
    }
}

public sealed class OtherSubscriber : IOtherTestEvent
{
    public int CallCount;
    public void OnOther() => CallCount++;
}

/// <summary>
/// Records every call made to an interface and returns default values. Lets us hand <see cref="EventService"/> its
/// dependencies (<see cref="ILoggerService"/>, <see cref="ILuaPatcher"/>) without a mocking library
/// and without touching the game's console or Harmony.
/// </summary>
public class RecordingProxy : DispatchProxy
{
    public readonly List<(string Method, object?[] Args)> Calls = new();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        Calls.Add((targetMethod?.Name ?? "?", args ?? Array.Empty<object?>()));
        var returnType = targetMethod?.ReturnType;
        if (returnType is null || returnType == typeof(void)) { return null; }
        if (returnType == typeof(bool)) { return false; }
        if (returnType == typeof(FluentResults.Result)) { return FluentResults.Result.Ok(); }
        return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
    }

    public static T Create<T>(out RecordingProxy recorder) where T : class
    {
        var proxy = Create<T, RecordingProxy>();
        recorder = (RecordingProxy)(object)proxy;
        return proxy;
    }
}

public static class EventServiceFactory
{
    public sealed record Created(EventService Service, RecordingProxy Logger, RecordingProxy Patcher);

    public static Created Create()
    {
        var logger = RecordingProxy.Create<ILoggerService>(out var loggerRecorder);
        var patcher = RecordingProxy.Create<ILuaPatcher>(out var patcherRecorder);
        return new Created(new EventService(logger, patcher), loggerRecorder, patcherRecorder);
    }
}
