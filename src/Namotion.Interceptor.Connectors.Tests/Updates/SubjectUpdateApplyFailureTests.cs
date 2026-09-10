using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Validation;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

public class SubjectUpdateApplyFailureTests
{
    [Fact]
    public void WhenOnePropertyThrows_ThenTheOthersStillApply()
    {
        // Arrange
        var update = CreateThrowingDeviceUpdate();

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var target = new ThrowingDevice(context)
        {
            ThrowingEnabled = true,
            ShouldThrow = name => name == nameof(ThrowingDevice.PropertyA)
        };

        // Act
        var exception = Assert.ThrowsAny<Exception>(
            () => target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local));

        // Assert
        Assert.False(target.PropertyA);
        Assert.True(target.PropertyB);
        Assert.Contains(nameof(ThrowingDevice.PropertyA), exception.ToString());
    }

    [Fact]
    public void WhenOnePropertyFails_ThenItsOwnExceptionIsThrownUnwrapped()
    {
        // Arrange
        var update = CreateThrowingDeviceUpdate();

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var target = new ThrowingDevice(context)
        {
            ThrowingEnabled = true,
            ShouldThrow = name => name == nameof(ThrowingDevice.PropertyA)
        };

        // Act & Assert
        // A lone failure keeps its own type so callers that catch a specific exception still work.
        var exception = Assert.Throws<InvalidOperationException>(
            () => target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local));

        Assert.Contains(nameof(ThrowingDevice.PropertyA), exception.Message);
    }

    [Fact]
    public void WhenSeveralPropertiesFail_ThenAnAggregateCarriesThemAll()
    {
        // Arrange
        var update = CreateThrowingDeviceUpdate();

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var target = new ThrowingDevice(context)
        {
            ThrowingEnabled = true,
            ShouldThrow = _ => true
        };

        // Act & Assert
        var exception = Assert.Throws<AggregateException>(
            () => target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local));

        Assert.Equal(2, exception.InnerExceptions.Count);

        // AggregateException appends each inner message, and ThrowingDevice's own message contains the
        // property name, so assert against the header the applier itself builds rather than the whole
        // message, which would pass even with the name list removed.
        var header = exception.Message.Split(" (")[0];
        Assert.StartsWith("2 property updates could not be applied: ", header);
        Assert.Contains(nameof(ThrowingDevice.PropertyA), header);
        Assert.Contains(nameof(ThrowingDevice.PropertyB), header);
    }

    [Fact]
    public void WhenCancellationIsThrown_ThenItPropagatesImmediatelyAndIsNotCollected()
    {
        // Arrange
        var update = CreateThrowingDeviceUpdate();

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var target = new ThrowingDevice(context);

        // Act & Assert
        // Cancellation must unwind the batch instead of being collected as an apply failure.
        Assert.Throws<OperationCanceledException>(
            () => target.ApplySubjectUpdate(
                update,
                DefaultSubjectFactory.Instance,
                ChangeOrigin.Local,
                (_, _) => throw new OperationCanceledException()));
    }

    [Fact]
    public void WhenAPropertyInsideACollectionItemFails_ThenTheRestOfTheUpdateStillApplies()
    {
        // Arrange
        var sourceContext = InterceptorSubjectContext.Create().WithRegistry();
        var source = new Person(sourceContext)
        {
            LastName = "Parent",
            Children =
            [
                new Person(sourceContext) { FirstName = "TooLongForTheTarget", LastName = "Child" }
            ]
        };

        var update = SubjectUpdate.CreateCompleteUpdate(source, []);

        // The target validates data annotations, so the child's over-long FirstName is refused
        // while every other property of the same update is written.
        var targetContext = InterceptorSubjectContext.Create().WithRegistry().WithDataAnnotationValidation();
        var target = new Person(targetContext);

        // Act
        var exception = Assert.ThrowsAny<Exception>(
            () => target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local));

        // Assert
        Assert.Contains(nameof(Person.FirstName), exception.Message);
        Assert.Equal("Parent", target.LastName);
        var child = Assert.Single(target.Children);
        Assert.Equal("Child", child.LastName);
        Assert.Null(child.FirstName);
    }

    [Fact]
    public void WhenACollectionUpdateItselfFails_ThenSiblingPropertiesStillApply()
    {
        // Arrange
        // Pins the per-property boundary: a throw raised by the collection machinery itself, rather than
        // by an item's own property write, is contained at the Children property and does not cost the
        // unrelated LastName sibling. It does still abandon the remaining items of that collection.
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var target = new Person(context);

        var update = new SubjectUpdate
        {
            Root = "1",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["1"] = new()
                {
                    [nameof(Person.LastName)] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "Parent"
                    },
                    [nameof(Person.Children)] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Collection,
                        Items =
                        [
                            new SubjectPropertyItemUpdate { Index = 5, Id = "2" }
                        ],
                        Count = 1 // index 5 is out of bounds for a declared count of 1
                    }
                },
                ["2"] = new()
                {
                    [nameof(Person.FirstName)] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "Kid"
                    }
                }
            }
        };

        // Act
        var exception = Assert.Throws<InvalidOperationException>(
            () => target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local));

        // Assert
        Assert.Contains("out of bounds", exception.Message);
        Assert.Equal("Parent", target.LastName);
        Assert.Empty(target.Children);
    }

    [Fact]
    public void WhenEveryPropertyFails_ThenTheApplierStillThrowsSoCallersCanRetry()
    {
        // Arrange
        // Non-goal guard: a source's initial state load relies on the applier throwing to drive its
        // reconnect-and-reload retry, so a fully failed batch must never be swallowed as success.
        var update = CreateThrowingDeviceUpdate();

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var target = new ThrowingDevice(context)
        {
            ThrowingEnabled = true,
            ShouldThrow = _ => true
        };

        // Act & Assert
        Assert.ThrowsAny<Exception>(
            () => target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local));
    }

    /// <summary>
    /// Builds an update by hand so it carries only the two device properties: a complete update
    /// would also carry the target's throw configuration and disarm it before PropertyA is applied.
    /// </summary>
    private static SubjectUpdate CreateThrowingDeviceUpdate()
    {
        return new SubjectUpdate
        {
            Root = "root",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["root"] = new()
                {
                    [nameof(ThrowingDevice.PropertyA)] = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Value, Value = true },
                    [nameof(ThrowingDevice.PropertyB)] = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Value, Value = true }
                }
            }
        };
    }
}
