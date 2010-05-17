﻿using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using Ncqrs.Eventing;

namespace Ncqrs.Domain
{
    /// <summary>
    /// The abstract concept of an aggregate root.
    /// </summary>
    public abstract class AggregateRoot : IEventSource
    {
        private Guid _id;

        /// <summary>
        /// Gets the globally unique identifier.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when setting this
        /// value when the version of this aggregate root is not 0 or this
        /// instance contains are any uncommitted events.</exception>
        public Guid Id
        {
            get { return _id; }
            protected set
            {
                Contract.Requires<InvalidOperationException>(Version == 0);
                Contract.Requires<InvalidOperationException>(_uncommittedEvent.Count == 0);

                _id = value;
            }
        }

        /// <summary>
        /// Holds the events that are not yet committed.
        /// </summary>
        private readonly Stack<DomainEvent> _uncommittedEvent = new Stack<DomainEvent>(0);

        /// <summary>
        /// Gets the current version of the instance as it is known in the event store.
        /// </summary>
        /// <value>
        /// An <see cref="long"/> representing the current version of this aggregate root.
        /// </value>
        public long Version
        {
            get
            {
                return InitialVersion + _uncommittedEvent.Count;
            }
        }

        /// <summary>
        /// Gets the initial version.
        /// <para>
        /// This represents the current version of this instance. When this instance was retrieved
        /// via history, it contains the version as it was at that time. For new instances this value is always 0.
        /// </para>
        /// 	<para>
        /// The version does not change until changes are accepted via the <see cref="AcceptChanges"/> method.
        /// </para>
        /// </summary>
        /// <value>The initial version.</value>
        public long InitialVersion
        {
            get; private set;
        }

        /// <summary>
        /// A list that contains all the event handlers.
        /// </summary>
        private readonly List<IDomainEventHandler> _eventHandlers = new List<IDomainEventHandler>();

        /// <summary>
        /// Initializes a new instance of the <see cref="AggregateRoot"/> class.
        /// </summary>
        protected AggregateRoot()
        {
            InitialVersion = 0;

            var idGenerator = NcqrsEnvironment.Get<IUniqueIdentifierGenerator>();
            Id = idGenerator.GenerateNewId();
        }

        [ContractInvariantMethod]
        private void ContractInvariants()
        {
            Contract.Invariant(_uncommittedEvent != null, "The member _unacceptedEvents should never be null.");
        }

        /// <summary>
        /// Initializes from history.
        /// </summary>
        /// <param name="history">The history.</param>
        protected internal virtual void InitializeFromHistory(IEnumerable<DomainEvent> history)
        {
            if (history == null) 
                throw new ArgumentNullException("history");
            if (history.Count() == 0)
                throw new ArgumentException("The provided history does not contain any historical event.", "history");
            if (Version != 0 || _uncommittedEvent.Count > 0)
                throw new InvalidOperationException("Cannot load from history when a event source is already loaded.");

            foreach (var historicalEvent in history)
            {
                ApplyEvent(historicalEvent, true);
                InitialVersion++; // TODO: Thought... couldn't we get this from the event?
            }
        }

        protected void RegisterHandler(IDomainEventHandler handler)
        {
            Contract.Requires<ArgumentNullException>(handler != null, "The handler cannot be null.");

            _eventHandlers.Add(handler);
        }

        protected virtual void HandleEvent(DomainEvent evnt)
        {
            Contract.Requires<ArgumentNullException>(evnt != null, "The evnt cannot be null.");
            Boolean handled = false;

            foreach (var handler in _eventHandlers)
            {
                handled |= handler.HandleEvent(evnt);
            }

            if (!handled)
                throw new EventNotHandledException(evnt);
        }

        protected void ApplyEvent(DomainEvent evnt)
        {
            ApplyEvent(evnt, false);
        }

        private void ApplyEvent(DomainEvent evnt, Boolean historical)
        {
            HandleEvent(evnt);

            if (!historical)
            {
                if (evnt.AggregateRootId != Guid.Empty)
                {
                    var message = String.Format("The {0} event cannot be applied to aggregate root {1} with id {2} "
                                  + "since it was already owned by event aggregate root with id {3}.",
                                  evnt.GetType().FullName, this.GetType().FullName, Id, evnt.AggregateRootId);
                    throw new InvalidOperationException(message);
                }

                evnt.AggregateRootId = this.Id;
                evnt.EventSequence = Version + _uncommittedEvent.Count + 1;
                _uncommittedEvent.Push(evnt);

                RegisterCurrentInstanceAsDirty();
            }
        }

        public IEnumerable<DomainEvent> GetUncommittedEvents()
        {
            Contract.Ensures(Contract.Result<IEnumerable<DomainEvent>>() != null, "The result of this method should never be null.");

            return _uncommittedEvent.ToArray();
        }

        IEnumerable<ISourcedEvent> IEventSource.GetUncommittedEvents()
        {
            // TODO: .net 4.0 co/con
            return GetUncommittedEvents().Cast<ISourcedEvent>();
        }

        public void AcceptChanges()
        {
            // Update the initial version.
            this.InitialVersion = Version;

            // Clear the unaccepted event list.
            _uncommittedEvent.Clear();
        }

        private void RegisterCurrentInstanceAsDirty()
        {
            if(UnitOfWork.Current == null)
                throw new NoUnitOfWorkAvailableInThisContextException();

            UnitOfWork.Current.RegisterDirtyInstance(this);
        }
    }
}