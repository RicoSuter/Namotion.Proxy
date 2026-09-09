using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class ScalarReconciliationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void WhenAGetterSupersedesAScalarReplacement_ThenItsHistoryMatchesASingleOccurrenceCollection(
        bool reattachOwner, bool throwAfterNestedWrite)
    {
        // Arrange
        string[] Run(bool collection)
        {
            var context = InterceptorSubjectContext.Create().WithRegistry();
            var root = new CallbackSpikeNode();
            root.AttachToContext(context);
            var old = new CallbackSpikeNode();
            var final = new CallbackSpikeNode();
            var trigger = new CallbackSpikeSegmentWrapper();
            var history = new List<string>();
            object Value(IInterceptorSubject subject) => collection ? new[] { subject } : subject;
            string Identity(IInterceptorSubject subject) => ReferenceEquals(subject, root) ? "root" :
                ReferenceEquals(subject, old) ? "old" : ReferenceEquals(subject, final) ? "final" : "trigger";
            var lifecycle = context.TryGetLifecycleInterceptor()!;
            lifecycle.SubjectAttached += change => history.Add(Identity(change.Subject) + ":attached");
            lifecycle.SubjectDetaching += change => history.Add(Identity(change.Subject) + ":detached");
            root.Payload = Value(old);
            history.Clear();
            var failure = new InvalidOperationException("getter failed after replacement");
            trigger.OnRead = () =>
            {
                if (trigger.GetReferenceCount() == 0) return;
                trigger.OnRead = null;
                if (reattachOwner) root.DetachFromContext(context);
                root.Payload = Value(final);
                if (reattachOwner) root.AttachToContext(context);
                if (throwAfterNestedWrite) throw failure;
            };

            // Act
            var exception = Record.Exception(() => root.Payload = Value(trigger));

            // Assert
            Assert.Same(throwAfterNestedWrite ? failure : null, exception);
            Assert.Null(old.TryGetContext());
            Assert.Null(trigger.TryGetContext());
            Assert.Equal(1, final.GetReferenceCount());
            var parent = Assert.Single(final.GetParents());
            Assert.Same(root, parent.Property.Subject);
            Assert.Equal(collection ? 0 : (object?)null, parent.Index);
            Assert.NotNull(context.GetService<ISubjectRegistry>().TryGetRegisteredSubject(final));
            root.Payload = null;
            Assert.Null(final.TryGetContext());
            root.DetachFromContext(context);
            Assert.Empty(context.GetService<ISubjectRegistry>().KnownSubjects);
            return history.ToArray();
        }

        // Act
        var scalarHistory = Run(collection: false);
        var collectionHistory = Run(collection: true);

        // Assert
        Assert.Equal(collectionHistory, scalarHistory);
    }
}
