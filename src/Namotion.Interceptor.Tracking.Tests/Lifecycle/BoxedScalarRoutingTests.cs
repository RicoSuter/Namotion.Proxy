using System.Collections.Concurrent;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class BoxedScalarRoutingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenACallbackWritesABoxedScalarInAnotherContext_ThenItDoesNotRequireAnotherTopologyGate(bool numeric)
    {
        // Arrange
        var sourceContext = InterceptorSubjectContext.Create().WithLifecycle();
        var targetContext = InterceptorSubjectContext.Create().WithLifecycle();
        var target = new ScalarSubject(numeric ? typeof(int) : typeof(string));
        target.AttachToContext(targetContext);
        object expected = numeric ? 42 : "updated";
        sourceContext.TryGetLifecycleInterceptor()!.SubjectAttached += _ => target.SetBoxed(expected);

        // Act
        var source = new Person(sourceContext);

        // Assert
        Assert.Equal(expected, target.Value);
        Assert.Same(targetContext, target.TryGetContext());
        Assert.Same(sourceContext, source.TryGetContext());
    }

    private sealed class ScalarSubject : IInterceptorSubject
    {
        private IInterceptorExecutor? _executor;
        private object? _value;
        public IInterceptorExecutor Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);
        public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();
        public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties { get; }
        public object? Value => _value;

        public ScalarSubject(Type type)
        {
            Properties = new Dictionary<string, SubjectPropertyMetadata>
            {
                [nameof(Value)] = new(nameof(Value), type, [],
                    subject => ((ScalarSubject)subject)._value,
                    (subject, value) => ((ScalarSubject)subject).SetBoxed(value),
                    isIntercepted: true, isDynamic: false)
            };
        }

        public void SetBoxed(object? value) => Executor.SetPropertyValue(nameof(Value), value, _value,
            (subject, incoming) => ((ScalarSubject)subject)._value = incoming);

        public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) => throw new NotSupportedException();
    }
}
