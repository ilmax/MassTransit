﻿namespace MassTransit.Azure.ServiceBus.Core.Transport
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Context;
    using Contexts;
    using GreenPipes;
    using GreenPipes.Agents;
    using Logging;
    using MassTransit.Scheduling;
    using Microsoft.Azure.ServiceBus;
    using Transports;


    /// <summary>
    /// Send messages to an azure transport using the message sender.
    ///
    /// May be sensible to create a IBatchSendTransport that allows multiple messages to be sent as a single batch (perhaps using Tx support?)
    /// </summary>
    public class ServiceBusSendTransport :
        Supervisor,
        ISendTransport
    {
        readonly ServiceBusSendTransportContext _context;

        public ServiceBusSendTransport(ServiceBusSendTransportContext context)
        {
            _context = context;

            Add(context.Source);
        }

        Task ISendTransport.Send<T>(T message, IPipe<SendContext<T>> pipe, CancellationToken cancellationToken)
        {
            var sendPipe = new SendPipe<T>(_context, message, pipe, cancellationToken);

            return _context.Source.Send(sendPipe, cancellationToken);
        }

        public ConnectHandle ConnectSendObserver(ISendObserver observer)
        {
            return _context.ConnectSendObserver(observer);
        }

        protected override Task StopSupervisor(StopSupervisorContext context)
        {
            LogContext.Debug?.Log("Stopping send transport: {Address}", _context.Address);

            return base.StopSupervisor(context);
        }


        struct SendPipe<T> :
            IPipe<SendEndpointContext>
            where T : class
        {
            readonly ServiceBusSendTransportContext _context;
            readonly T _message;
            readonly CancellationToken _cancellationToken;
            readonly IPipe<SendContext<T>> _pipe;

            public SendPipe(ServiceBusSendTransportContext context, T message, IPipe<SendContext<T>> pipe, CancellationToken cancellationToken)
            {
                _context = context;
                _message = message;
                _cancellationToken = cancellationToken;
                _pipe = pipe;
            }

            public async Task Send(SendEndpointContext clientContext)
            {
                LogContext.SetCurrentIfNull(_context.LogContext);

                var context = new AzureServiceBusSendContext<T>(_message, _cancellationToken);

                await _pipe.Send(context).ConfigureAwait(false);

                CopyIncomingIdentifiersIfPresent(context);

                var activity = LogContext.IfEnabled(OperationName.Transport.Send)?.StartSendActivity(context,
                    (nameof(context.PartitionKey), context.PartitionKey),
                    (nameof(context.SessionId), context.SessionId));
                try
                {
                    if (IsCancelScheduledSend(context, out var sequenceNumber))
                    {
                        await CancelScheduledSend(clientContext, sequenceNumber).ConfigureAwait(false);

                        return;
                    }

                    if (context.ScheduledEnqueueTimeUtc.HasValue)
                    {
                        var scheduled = await ScheduleSend(clientContext, context).ConfigureAwait(false);
                        if (scheduled)
                            return;
                    }

                    if (_context.SendObservers.Count > 0)
                        await _context.SendObservers.PreSend(context).ConfigureAwait(false);

                    var brokeredMessage = CreateBrokeredMessage(context);

                    await clientContext.Send(brokeredMessage).ConfigureAwait(false);

                    context.LogSent();

                    if (_context.SendObservers.Count > 0)
                        await _context.SendObservers.PostSend(context).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (_context.SendObservers.Count > 0)
                        await _context.SendObservers.SendFault(context, ex).ConfigureAwait(false);

                    throw;
                }
                finally
                {
                    activity?.Stop();
                }
            }

            public void Probe(ProbeContext context)
            {
            }

            static async Task<bool> ScheduleSend(SendEndpointContext clientContext, AzureServiceBusSendContext<T> context)
            {
                var now = DateTime.UtcNow;

                var enqueueTimeUtc = context.ScheduledEnqueueTimeUtc.Value;
                if (enqueueTimeUtc < now)
                {
                    LogContext.Debug?.Log("The scheduled time was in the past, sending: {ScheduledTime}", context.ScheduledEnqueueTimeUtc);

                    return false;
                }

                try
                {
                    var brokeredMessage = CreateBrokeredMessage(context);

                    var sequenceNumber = await clientContext.ScheduleSend(brokeredMessage, enqueueTimeUtc).ConfigureAwait(false);

                    context.SetScheduledMessageId(sequenceNumber);

                    context.LogScheduled(enqueueTimeUtc);

                    return true;
                }
                catch (ArgumentOutOfRangeException)
                {
                    LogContext.Debug?.Log("The scheduled time was rejected by the server, sending: {MessageId}", context.MessageId);

                    return false;
                }
            }

            static async Task CancelScheduledSend(SendEndpointContext clientContext, long sequenceNumber)
            {
                try
                {
                    await clientContext.CancelScheduledSend(sequenceNumber).ConfigureAwait(false);

                    LogContext.Debug?.Log("Canceled scheduled message {SequenceNumber} {EntityPath}", sequenceNumber, clientContext.EntityPath);
                }
                catch (MessageNotFoundException exception)
                {
                    LogContext.Warning?.Log(exception, "The scheduled message was not found: {SequenceNumber} {EntityPath}", sequenceNumber,
                        clientContext.EntityPath);
                }
            }

            bool IsCancelScheduledSend(AzureServiceBusSendContext<T> context, out long sequenceNumber)
            {
                if (_message is CancelScheduledMessage cancelScheduledMessage)
                {
                    if (context.TryGetScheduledMessageId(out sequenceNumber)
                        || context.TryGetSequenceNumber(cancelScheduledMessage.TokenId, out sequenceNumber))
                        return true;
                }

                sequenceNumber = default;
                return false;
            }

            static Message CreateBrokeredMessage(AzureServiceBusSendContext<T> context)
            {
                var brokeredMessage = new Message(context.Body) {ContentType = context.ContentType.MediaType};

                brokeredMessage.UserProperties.Set(context.Headers);

                if (context.TimeToLive.HasValue)
                    brokeredMessage.TimeToLive = context.TimeToLive.Value;

                if (context.MessageId.HasValue)
                    brokeredMessage.MessageId = context.MessageId.Value.ToString("N");

                if (context.CorrelationId.HasValue)
                    brokeredMessage.CorrelationId = context.CorrelationId.Value.ToString("N");

                if (context.PartitionKey != null)
                    brokeredMessage.PartitionKey = context.PartitionKey;

                if (!string.IsNullOrWhiteSpace(context.SessionId))
                {
                    brokeredMessage.SessionId = context.SessionId;

                    if (context.ReplyToSessionId == null)
                        brokeredMessage.ReplyToSessionId = context.SessionId;
                }

                if (context.ReplyToSessionId != null)
                    brokeredMessage.ReplyToSessionId = context.ReplyToSessionId;

                return brokeredMessage;
            }

            static void CopyIncomingIdentifiersIfPresent(AzureServiceBusSendContext<T> context)
            {
                if (context.TryGetPayload<ConsumeContext>(out var consumeContext)
                    && consumeContext.TryGetPayload<BrokeredMessageContext>(out var brokeredMessageContext))
                {
                    if (context.SessionId == null)
                    {
                        if (brokeredMessageContext.ReplyToSessionId != null)
                            context.SessionId = brokeredMessageContext.ReplyToSessionId;
                        else if (brokeredMessageContext.SessionId != null)
                            context.SessionId = brokeredMessageContext.SessionId;
                    }

                    if (context.PartitionKey == null && brokeredMessageContext.PartitionKey != null)
                        context.PartitionKey = brokeredMessageContext.PartitionKey;
                }
            }
        }
    }
}
