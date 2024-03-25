﻿using Namotion.Proxy.Abstractions;
using Namotion.Proxy.ChangeTracking;

namespace Namotion.Proxy.Tests.Handlers
{
    public class PropertyChangedHandlersHandlerTests
    {
        [Fact]
        public void WhenPropertyIsChanged_ThenChangeHandlerIsTriggered()
        {
            // Arrange
            var changes = new List<ProxyPropertyChanged>();
            var context = ProxyContext
                .CreateBuilder()
                .WithPropertyChangedHandlers()
                .Build();

            context
                .GetHandler<IProxyPropertyChangedHandler>()
                .Subscribe(changes.Add);

            // Act
            var person = new Person(context);
            person.FirstName = "Rico";

            // Assert
            Assert.Contains(changes, c => 
                c.Property.Name == "FirstName" &&
                c.OldValue is null &&
                c.NewValue?.ToString() == "Rico");
        }
    }
}