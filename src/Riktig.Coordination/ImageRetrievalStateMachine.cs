﻿namespace Riktig.Coordination
{
    using System;
    using Automatonymous;
    using Automatonymous.RepositoryConfigurators;
    using Contracts.Api;
    using Contracts.Services.Events;
    using MassTransit;
    using MassTransit.Logging;
    using RapidTransit.Core;


    public class ImageRetrievalStateMachine :
        AutomatonymousStateMachine<ImageRetrievalState>
    {
        static readonly ILog _log = Logger.Get<ImageRetrievalStateMachine>();

        readonly IStateMachineActivityFactory _activityFactory;

        public ImageRetrievalStateMachine(IStateMachineActivityFactory activityFactory)
        {
            _activityFactory = activityFactory;

            State(() => Pending);
            State(() => Available);
            State(() => Faulted);

            Event(() => Requested);
            Event(() => Retrieved);
            Event(() => RetrieveFailed);
            Event(() => NotFound);

            Initially(
                When(Requested)
                    .Then((state, message) =>
                        {
                            _log.DebugFormat("Requested: {0} ({1})", message.SourceAddress, message.RequestId);

                            state.Created = DateTime.UtcNow;
                            state.FirstRequested = message.Timestamp;
                            state.SourceAddress = message.SourceAddress;
                            state.LocalAddress = new Uri("urn:unknown");
                        })
                    .Then(() => _activityFactory.GetActivity<SendRetrieveImageCommandActivity, ImageRetrievalState>())
                    .Publish((_, message) => new ImageRequestedEvent(message.RequestId, message.SourceAddress))
                    .TransitionTo(Pending));

            During(Pending,
                // this is to handle the contract of publishing the event but an existing request is 
                // already pending
                When(Requested)
                    .Then(
                        (state, message) =>
                            { _log.DebugFormat("Pending: {0} ({1})", state.SourceAddress, message.RequestId); })
                    .Publish((_, message) => new ImageRequestedEvent(message.RequestId, message.SourceAddress)),
                // this event is observed when the service completes the image retrieval
                When(Retrieved)
                    .Then((state, message) =>
                        {
                            _log.DebugFormat("Retrieved: {0} ({1})", message.LocalAddress, state.CorrelationId);

                            state.LastRetrieved = message.Timestamp;
                            state.LocalAddress = message.LocalAddress;
                            state.ContentType = message.ContentType;
                            state.ContentLength = message.ContentLength;
                        })
                    .TransitionTo(Available),
                When(RetrieveFailed)
                    .Then((state, message) => state.Reason = message.Reason)
                    .TransitionTo(Faulted)
                    .Publish((_, message) => new ImageRequestFaultedEvent(message.SourceAddress, message.Reason))
                );

            During(Available,
                When(Requested)
                    .Then((state, message) =>
                        {
                            _log.DebugFormat("Available: {0} {2} ({1})", state.LocalAddress, message.RequestId,
                                state.SourceAddress);
                        })
                    .Publish((_, message) => new ImageRequestedEvent(message.RequestId, message.SourceAddress))
                    .Respond((state, message) =>
                             new ImageRequestCompletedEvent(state.ContentLength.Value, state.ContentType,
                                 state.LocalAddress, state.SourceAddress, state.LastRetrieved.Value)));
        }


        public State Pending { get; private set; }
        public State Available { get; private set; }
        public State Faulted { get; private set; }

        public Event<RequestImage> Requested { get; private set; }
        public Event<ImageRetrieved> Retrieved { get; private set; }
        public Event<ImageRetrievalFailed> RetrieveFailed { get; private set; }
        public Event<ImageNotFound> NotFound { get; private set; }

        public void ConfigureStateMachineCorrelations(StateMachineSagaRepositoryConfigurator<ImageRetrievalState> r)
        {
            r.Correlate(Requested, (state, message) => state.SourceAddress == message.SourceAddress)
             .SelectCorrelationId(message => message.RequestId);

            r.Correlate(Retrieved, (state, message) => state.SourceAddress == message.SourceAddress);

            r.Correlate(NotFound, (state, message) => state.SourceAddress == message.SourceAddress);

            r.Correlate(RetrieveFailed, (state, message) => state.SourceAddress == message.SourceAddress);
        }


        class ImageRequestCompletedEvent :
            ImageRequestCompleted
        {
            public ImageRequestCompletedEvent(int contentLength, string contentType, Uri localAddress, Uri sourceAddress,
                DateTime retrieved)
            {
                EventId = NewId.NextGuid();
                Timestamp = DateTime.UtcNow;

                ContentLength = contentLength;
                ContentType = contentType;
                LocalAddress = localAddress;
                SourceAddress = sourceAddress;
                Retrieved = retrieved;
            }

            public Guid EventId { get; private set; }
            public DateTime Timestamp { get; private set; }
            public DateTime Retrieved { get; private set; }
            public Uri SourceAddress { get; private set; }
            public Uri LocalAddress { get; private set; }
            public string ContentType { get; private set; }
            public int ContentLength { get; private set; }
        }


        class ImageRequestFaultedEvent :
            ImageRequestFaulted
        {
            public ImageRequestFaultedEvent(Uri sourceAddress, string reason)
            {
                EventId = NewId.NextGuid();
                Timestamp = DateTime.UtcNow;

                SourceAddress = sourceAddress;
                Reason = reason;
            }

            public Guid EventId { get; private set; }
            public DateTime Timestamp { get; private set; }
            public Uri SourceAddress { get; private set; }
            public string Reason { get; private set; }
        }


        class ImageRequestedEvent :
            ImageRequested
        {
            public ImageRequestedEvent(Guid originatingCommandId, Uri sourceAddress)
            {
                OriginatingCommandId = originatingCommandId;
                SourceAddress = sourceAddress;
            }

            public Guid EventId { get; private set; }
            public DateTime Timestamp { get; private set; }
            public Guid OriginatingCommandId { get; private set; }
            public Uri SourceAddress { get; private set; }
        }
    }
}