﻿using Namotion.Proxy.Abstractions;
using Namotion.Proxy.ChangeTracking;

namespace Namotion.Proxy.Tests.Handlers
{
    public class DetectDerivedPropertyChangesHandlerTests
    {
        [Fact]
        public void WhenChangingPropertyWhichIsUsedInDerivedProperty_ThenDerivedPropertyIsChanged()
        {
            // Arrange
            var changes = new List<ProxyPropertyChanged>();
            var context = ProxyContext
                .CreateBuilder()
                .WithDerivedPropertyChangeDetection()
                .Build();

            context
                .GetHandler<IProxyPropertyChangedHandler>()
                .Subscribe(changes.Add);

            // Act
            var person = new Person(context);
            person.FirstName = "Rico";
            person.LastName = "Suter";

            // Assert
            Assert.Contains(changes, c =>
                c.PropertyName == nameof(Person.FullName) &&
                c.OldValue?.ToString() == " " &&
                c.NewValue?.ToString() == "Rico ");

            Assert.Contains(changes, c => 
                c.PropertyName == nameof(Person.FullName) &&
                c.OldValue?.ToString() == "Rico " && 
                c.NewValue?.ToString() == "Rico Suter");
        }
    }
}