module NeuralFish.Core

open NeuralFish.Types
open NeuralFish.Exceptions

let sigmoid = (fun x -> 1.0 / (1.0 + exp(-x)))

let defaultInfoLog (message : string) =
  message |> System.Console.WriteLine

let rec waitOnNeuralNetwork (checkActuators : bool) (maybeStopwatch : (int*System.Diagnostics.Stopwatch) option) (neuralNetworkToWaitOn : LiveNeuralNetwork) =
  let checkIfNeuralNetworkIsBusy neuralNetwork =
    let isNodeReady (neuron : NeuronInstance) =
      let maybeNodeStatus =
        (fun r -> GetNodeStatus(checkActuators, r))
        |> (fun msg -> neuron.TryPostAndReply(msg, timeout=500))
      match maybeNodeStatus with
      | None -> raise <| NeuronInstanceUnavailableException "Cortex - Neuron instance is not available when trying to check actuators"
      | Some nodeStatus -> 
        match nodeStatus with
        | NodeIsBusy -> false
        | NodeIsReady -> true
    neuralNetwork
    |> Array.exists(fun node -> isNodeReady node |> not)
  if neuralNetworkToWaitOn |> checkIfNeuralNetworkIsBusy then
    match maybeStopwatch with
    | Some (thinkTimeout, stopwatch) ->
      let timeIsUp = System.BitConverter.DoubleToInt64Bits(stopwatch.Elapsed.TotalMilliseconds) >= (int64 thinkTimeout)
      if timeIsUp then
        false
      else
        waitOnNeuralNetwork checkActuators maybeStopwatch neuralNetworkToWaitOn
    | None ->
        waitOnNeuralNetwork checkActuators maybeStopwatch neuralNetworkToWaitOn
  else
    true
let killNeuralNetwork (liveNeurons : LiveNeuralNetwork) =
  let killNeuralNetwork (neuralNetworkToKill : LiveNeuralNetwork) =
    neuralNetworkToKill
    |> Array.Parallel.iter(fun neuron -> neuron.TryPostAndReply (Die, timeout=500) |> ignore)

  liveNeurons
  |> waitOnNeuralNetwork false None
  |> ignore
  liveNeurons
  |> killNeuralNetwork

let activateActuators (neuralNetwork : LiveNeuralNetwork) =
  let activateActuator (liveNeuron : NeuronInstance) =
    let didPost = ActivateActuator |> liveNeuron.TryPostAndReply
    match didPost with
    | None ->
      raise <| NeuronInstanceUnavailableException "Core - Neuron unable to activate due to instance being unavailable"
    | Some _ -> ()
  neuralNetwork
  |> Array.Parallel.iter activateActuator

let synchronize (sensor : NeuronInstance) =
  //TODO have this timeout
  Sync |> sensor.PostAndReply

let synchronizeNN (neuralNetwork : LiveNeuralNetwork) =
  let synchronizeMap instance =
    instance |> synchronize
  neuralNetwork
  |> Array.Parallel.iter synchronizeMap

let synapseDotProduct (weightedSynapses : WeightedSynapses) =
  let rec loop synapses =
    match synapses with
    | [] -> 0.0
    | (value : Synapse, inboundConnection : InboundNeuronConnection)::tail -> value*inboundConnection.Weight + (loop tail)
  weightedSynapses |> Seq.toList |> loop


let createNeuronFromRecord (activationFunction : ActivationFunction) (record : NodeRecord) : NeuronType =
  {
    Record = record
    ActivationFunction = activationFunction
  } |> NeuronType.Neuron

let createNeuron id layer activationFunction activationFunctionId bias learningAlgorithm =
   {
     NodeId = id
     Layer = layer
     NodeType = NodeRecordType.Neuron
     InboundConnections = Map.empty
     Bias = Some bias
     ActivationFunctionId = Some activationFunctionId
     SyncFunctionId = None
     OutputHookId = None
     MaximumVectorLength = None
     NeuronLearningAlgorithm = learningAlgorithm
   } |> createNeuronFromRecord activationFunction

let createSensorFromRecord syncFunction record =
  {
    Record = record
    SyncFunction = syncFunction
  } |> Sensor

let createSensor id syncFunction syncFunctionId maximumVectorLength =
  {
    NodeId = id
    Layer = 0
    NodeType = NodeRecordType.Sensor 0
    InboundConnections = Map.empty
    Bias = None
    ActivationFunctionId = None
    SyncFunctionId = Some syncFunctionId
    OutputHookId = None
    MaximumVectorLength = Some maximumVectorLength
    NeuronLearningAlgorithm = NoLearning
  } |> createSensorFromRecord syncFunction

let createActuatorFromRecord outputHook record =
  {
    Record = record
    OutputHook = outputHook
  } |> Actuator

let createActuator id layer outputHook outputHookId =
  {
    NodeId = id
    Layer = layer
    NodeType = NodeRecordType.Actuator
    InboundConnections = Map.empty
    Bias = None
    ActivationFunctionId = None
    SyncFunctionId = None
    OutputHookId = Some outputHookId
    MaximumVectorLength = None
    NeuronLearningAlgorithm = NoLearning
  } |> createActuatorFromRecord outputHook

let connectNodeToNode toNodeType (toNodeId, (toNodeLayer, toNode)) weight (fromNodeId, (_,fromNode : NeuronInstance)) =
  {
    ConnectionOrderOption = None
    ToNodeId = toNodeId
    InitialWeight = weight
  }
  |> (fun partialOutboundConnection r -> ((toNodeType,toNode,toNodeLayer,partialOutboundConnection),r) |> NeuronActions.AddOutboundConnection)
  |> fromNode.PostAndReply

let connectNodeToNeuron toNode weight fromNode =
  connectNodeToNode NodeRecordType.Neuron toNode weight fromNode

let connectNodeToActuator actuator fromNode  =
    connectNodeToNode NodeRecordType.Actuator actuator 0.0 fromNode

let connectSensorToNode toNode weights sensor =
 let createConnectionsFromWeight toNode fromNode weight =
   sensor |> connectNodeToNeuron toNode weight
 weights |> Seq.iter (sensor |> createConnectionsFromWeight toNode )

let createNeuronInstance infoLog neuronType =
  let nodeId, nodeLayer =
    match neuronType with
      | Neuron props ->
        props.Record.NodeId, props.Record.Layer
      | Sensor props ->
        props.Record.NodeId, props.Record.Layer
      | Actuator props ->
        props.Record.NodeId, props.Record.Layer
  let isBarrierSatisifed (inboundNeuronConnections : InboundNeuronConnections) (barrier : IncomingSynapses) =
    inboundNeuronConnections
    |> Array.forall(fun (connection) -> barrier |> Map.containsKey connection.NeuronConnectionId)
  let addBias bias outputVal =
    outputVal + bias
  let sendRecurrentSignal activationOption neuronConnectionId (toNodeId, toNode : NeuronInstance) =
    sprintf "Node %A Sending recurrent blank synapse to %A via %A" nodeId toNodeId neuronConnectionId |> infoLog
    (neuronConnectionId, 0.0, activationOption)
    |> ReceiveInput
    |> toNode.Post
  let activateNeuron (barrier : WeightedSynapses) (outboundConnections : NeuronConnections) (neuronProps : NeuronProperties) =
    let sendSynapseToNeurons (outputNeurons : NeuronConnections) outputValue =
      let sendSynapseToNeuron outputValue outputNeuronConnection =
        (outputNeuronConnection.NeuronConnectionId, outputValue, ActivateIfBarrierIsFull)
        |> ReceiveInput
        |> outputNeuronConnection.Neuron.Post
      outputNeurons
      |> Array.iter (sendSynapseToNeuron outputValue)
      outputValue
    let logNeuronOutput nodeId activationFunctionId bias outputValue =
      sprintf "Neuron %A is outputting %A after activation %A and bias %A" nodeId outputValue activationFunctionId bias
      |> infoLog
      outputValue

    let someBias =
      match neuronProps.Record.Bias with
      | Some bias -> bias
      | None -> 0.0
    barrier
    |> synapseDotProduct
    |> addBias someBias
    |> neuronProps.ActivationFunction
    |> logNeuronOutput neuronProps.Record.NodeId neuronProps.Record.ActivationFunctionId neuronProps.Record.Bias
    |> sendSynapseToNeurons outboundConnections
  let activateActuator (barrier : IncomingSynapses) actuatorProps =
      let logActuatorOutput outputHookId outputValue =
        sprintf "Actuator %A is outputting %A with output hook %A" nodeId outputValue outputHookId
        |> infoLog
        outputValue
      barrier
      |> Map.toSeq
      |> Seq.sumBy (fun (_,value) -> value)
      |> logActuatorOutput actuatorProps.Record.OutputHookId
      |> actuatorProps.OutputHook

  let neuronInstance = NeuronInstance.Start(fun inbox ->
    let rec loop (barrier : AxonHillockBarrier)
                   (inboundConnections : InboundNeuronConnections)
                     (outboundConnections : NeuronConnections)
                       (maximumVectorLength : int)
                         (maybeCortex : bool option)
                           (recurrentOutboundConnections : RecurrentNeuronConnections)
                             (overflowBarrier : AxonHillockBarrier) =
      async {
        let! someMsg = inbox.TryReceive 250
        match someMsg with
        | None ->
          return! loop barrier inboundConnections outboundConnections maximumVectorLength maybeCortex recurrentOutboundConnections overflowBarrier
        | Some msg ->
          match msg with
          | Sync replyChannel ->
            match neuronType with
            | Neuron _ ->
              replyChannel.Reply ()
              return! loop barrier inboundConnections outboundConnections maximumVectorLength maybeCortex recurrentOutboundConnections overflowBarrier
            | Actuator _ ->
              replyChannel.Reply ()
              return! loop barrier inboundConnections outboundConnections maximumVectorLength maybeCortex recurrentOutboundConnections overflowBarrier
            | Sensor props ->
              let inflateData expectedVectorLength (dataStream : NeuronOutput seq) =
                let inflatedData =
                  let difference = expectedVectorLength - (dataStream |> Seq.length)
                  [0..difference]
                  |> Seq.map (fun _ -> 0.0)
                Seq.append dataStream inflatedData
              let deflatedDataStream = props.SyncFunction()
              let newMaximumVectorLength =
                let actualDataVectorLength = deflatedDataStream |> Seq.length
                if (actualDataVectorLength > maximumVectorLength) then
                  actualDataVectorLength
                else
                  maximumVectorLength
              let dataStream =
                deflatedDataStream
                |> inflateData (outboundConnections |> Seq.length)
              let rec processSensorSync dataStream remainingConnections =
                if (dataStream |> Seq.isEmpty || remainingConnections |> Seq.isEmpty) then
                  ()
                else
                  let sendSynapseToNeuron (neuron : NeuronInstance) neuronConnectionId outputValue =
                    (neuronConnectionId, outputValue, ActivateIfBarrierIsFull)
                    |> ReceiveInput
                    |> neuron.Post
                  let data = dataStream |> Seq.head
                  let connection = remainingConnections |> Seq.head
                  sprintf "Node %A Sending %A to node %A via connection %A" nodeId data connection.NodeId connection.NeuronConnectionId
                  |> infoLog
                  data |> sendSynapseToNeuron connection.Neuron connection.NeuronConnectionId
                  let newDataStream = (dataStream |> Seq.tail)
                  let updatedRemainingConnections = (remainingConnections |> Seq.tail)
                  processSensorSync newDataStream updatedRemainingConnections
              if outboundConnections |> Seq.isEmpty then
                let exceptionMsg = sprintf "Sensor %A does not have any outbound connections" nodeId
                raise <| SensorInstanceDoesNotHaveAnyOutboundConnections(exceptionMsg)
              else
                let orderedConnections = outboundConnections |> Seq.sortBy(fun connection -> connection.ConnectionOrder)
                processSensorSync dataStream orderedConnections
                replyChannel.Reply ()
              return! loop barrier inboundConnections outboundConnections newMaximumVectorLength maybeCortex recurrentOutboundConnections overflowBarrier
          | ReceiveInput (neuronConnectionId, package, neuronActivationOption) ->
            let processLearning learningAlgorithm (weightedSynapses : WeightedSynapses) (neuronOutput : NeuronOutput) : InboundNeuronConnections =
              let processLearningForConnection ((synapse,inboundConnection) : WeightedSynapse) =
                match learningAlgorithm with
                | NoLearning -> inboundConnection
                | Hebbian learningRateCoefficient ->
                  let newWeight = inboundConnection.Weight + learningRateCoefficient*synapse*neuronOutput
                  { inboundConnection with
                      Weight = newWeight
                  }
              weightedSynapses
              |> Seq.map processLearningForConnection
              |> Seq.toArray

            let updatedOverflowBarrier, (updatedBarrier : AxonHillockBarrier) =
              if barrier |> Map.containsKey neuronConnectionId then
                let updatedOverflowBarrier =
                  overflowBarrier
                  |> Map.add neuronConnectionId package
                updatedOverflowBarrier, barrier
              else
                let updatedBarrier : AxonHillockBarrier =
                  barrier
                  |> Map.add neuronConnectionId package
                overflowBarrier, updatedBarrier

            let activateIfBarrierIsFull, activateIfNeuronHasOneConnection =
              match neuronActivationOption with
              | ActivateIfBarrierIsFull -> true, false
              | ActivateIfNeuronHasOneConnection ->
                let neuronOnlyHasOneConnection = inboundConnections |> Seq.length |> (fun x -> x = 1)
                false, neuronOnlyHasOneConnection
              | DoNotActivate -> false, false
            match neuronType with
            | Neuron props ->
              //This is to solve the case of a single recurrent inbound connection
              //Neuron was never firing and NN would become deadlocked
              if ((activateIfNeuronHasOneConnection || activateIfBarrierIsFull) && updatedBarrier |> isBarrierSatisifed inboundConnections) then
                sprintf "Barrier is satisfied for Neuron %A. Received %A from %A" props.Record.NodeId package neuronConnectionId |> infoLog
                let weightedSynapses : WeightedSynapses =
                  let getWeightedSynapse (neuronConnection : InboundNeuronConnection) : WeightedSynapse =
                    let synapse =
                      //TODO updated this exception to be relevant. Also the auto restart logic will need to be connected here
                      match updatedBarrier |> Map.tryFind neuronConnection.NeuronConnectionId with
                      | None -> raise <| MissingInboundConnectionException(sprintf "Neuron %A is missing inbound connection %A" nodeId neuronConnectionId)
                      | Some x -> x
                    synapse, neuronConnection
                  inboundConnections
                  |> Seq.map getWeightedSynapse
                let neuronOutput = props |> activateNeuron weightedSynapses outboundConnections
                let updatedInboundConnections =
                  processLearning props.Record.NeuronLearningAlgorithm weightedSynapses neuronOutput
                return! loop updatedOverflowBarrier updatedInboundConnections outboundConnections maximumVectorLength maybeCortex recurrentOutboundConnections Map.empty
              else
                sprintf "Barrier not satisfied for Neuron %A. Received %A from %A" props.Record.NodeId package neuronConnectionId |> infoLog
                return! loop updatedBarrier inboundConnections outboundConnections maximumVectorLength maybeCortex recurrentOutboundConnections updatedOverflowBarrier
            | Actuator props ->
              if (activateIfBarrierIsFull && updatedBarrier |> isBarrierSatisifed inboundConnections) then
                match maybeCortex with
                | None ->
                  sprintf "Barrier is satisifed for Actuator %A" props.Record.NodeId |> infoLog
                  props |> activateActuator updatedBarrier
                  return! loop updatedOverflowBarrier inboundConnections outboundConnections maximumVectorLength maybeCortex recurrentOutboundConnections Map.empty
                | Some _ ->
                  sprintf "Barrier is satisifed for Actuator %A. Not activating due to registered cortex. Waiting for a signal from the cortex" props.Record.NodeId |> infoLog
                  let readyToActivate = Some true
                  return! loop updatedBarrier inboundConnections outboundConnections maximumVectorLength readyToActivate recurrentOutboundConnections updatedOverflowBarrier
              else
                sprintf "Node %A not activated. Received %A from %A" nodeId package neuronConnectionId |> infoLog
                return! loop updatedBarrier inboundConnections outboundConnections maximumVectorLength maybeCortex recurrentOutboundConnections updatedOverflowBarrier
            | Sensor _ -> raise <| System.Exception("Sensor should not receive input")
          | NeuronActions.AddOutboundConnection ((toNodeType,toNode,outboundLayer,partialOutboundConnection),replyChannel) ->
              let neuronConnectionId = System.Guid.NewGuid()
              let connectionOrder =
                match neuronType with
                | Sensor _ ->
                  match partialOutboundConnection.ConnectionOrderOption with
                  | Some connectionOrder -> connectionOrder
                  | None ->  outboundConnections |> Seq.length
                | _ -> 0
              let newOutboundConnection =
                {
                 NeuronConnectionId = neuronConnectionId
                 ConnectionOrder = connectionOrder
                 InitialWeight = partialOutboundConnection.InitialWeight
                 NodeId = partialOutboundConnection.ToNodeId
                 Neuron = toNode
                }
              let updatedOutboundConnections =
                outboundConnections
                |> Array.append [|newOutboundConnection|]

              ({
                ConnectionOrder = match neuronType with | Sensor _ -> Some connectionOrder | _ -> None
                NeuronConnectionId = neuronConnectionId
                FromNodeId = nodeId
                InitialWeight = partialOutboundConnection.InitialWeight
                Weight = partialOutboundConnection.InitialWeight
              }, replyChannel)
              |> NeuronActions.AddInboundConnection
              |> toNode.Post

              let isRecurrentConnection = 
                let isNeuron =
                  match neuronType with
                  | Neuron _ -> true
                  | _ -> false
                toNodeType = NodeRecordType.Neuron 
                && isNeuron 
                && nodeLayer >= outboundLayer

              let updatedRecurrentOutboundConnections =
                if isRecurrentConnection then
                  recurrentOutboundConnections
                  |> Array.append [|newOutboundConnection|]
                else
                  recurrentOutboundConnections

              sprintf "Node %A is adding Node %A as an outbound connection %A with weight %A" neuronType partialOutboundConnection.ToNodeId neuronConnectionId partialOutboundConnection.InitialWeight
              |> infoLog
              return! loop barrier inboundConnections updatedOutboundConnections maximumVectorLength maybeCortex updatedRecurrentOutboundConnections overflowBarrier
            | NeuronActions.AddInboundConnection (inboundConnection, replyChannel) ->
              let updatedInboundConnections =
                Array.append inboundConnections [|inboundConnection|]
              replyChannel.Reply()
              sprintf "Node %A Added inbound neuron %A connection %A" nodeId inboundConnection.FromNodeId inboundConnection.NeuronConnectionId
              |> infoLog
              return! loop barrier updatedInboundConnections outboundConnections maximumVectorLength maybeCortex recurrentOutboundConnections overflowBarrier
            | GetNodeRecord replyChannel ->
              async {
                let inactiveConnections : NodeRecordConnections =
                  let createInactiveConnection (inboundNeuronConnection : InboundNeuronConnection) : NeuronConnectionId*InactiveNeuronConnection =
                    inboundNeuronConnection.NeuronConnectionId, {
                      NodeId = inboundNeuronConnection.FromNodeId
                      Weight = inboundNeuronConnection.InitialWeight
                      ConnectionOrder = inboundNeuronConnection.ConnectionOrder
                    }
                  inboundConnections
                  |> Seq.map createInactiveConnection
                  |> Map.ofSeq
                let nodeRecord =
                  match neuronType with
                  | Neuron props -> props.Record
                  | Sensor props ->
                    let maximumVectorLengthToRecord =
                      match props.Record.MaximumVectorLength with
                        | Some previousMaximumVectorLength ->
                          if (maximumVectorLength > previousMaximumVectorLength) then
                            maximumVectorLength
                          else
                            previousMaximumVectorLength
                        | None -> maximumVectorLength
                    let numberOfOutboundConnections = outboundConnections |> Seq.length
                    { props.Record with NodeType = NodeRecordType.Sensor numberOfOutboundConnections; MaximumVectorLength = Some maximumVectorLengthToRecord }
                  | Actuator props -> props.Record
                let nodeRecordWithConnections = { nodeRecord with InboundConnections = inactiveConnections}
                nodeRecordWithConnections |> replyChannel.Reply
              } |> Async.Start |> ignore
              return! loop barrier inboundConnections outboundConnections maximumVectorLength maybeCortex recurrentOutboundConnections overflowBarrier
            | Die replyChannel ->
              replyChannel.Reply()
            | RegisterCortex (_,replyChannel) ->
              match neuronType with
              | Actuator _ ->
                let someReadyToActivate = Some false
                replyChannel.Reply ()
                return! loop barrier inboundConnections outboundConnections maximumVectorLength someReadyToActivate recurrentOutboundConnections overflowBarrier
              | _ ->
                replyChannel.Reply ()
                return! loop barrier inboundConnections outboundConnections maximumVectorLength maybeCortex recurrentOutboundConnections overflowBarrier
            | ActivateActuator replyChannel ->
              match maybeCortex with
              | None ->
                replyChannel.Reply()
                return! loop barrier inboundConnections outboundConnections maximumVectorLength maybeCortex recurrentOutboundConnections overflowBarrier
              | Some readyToActivate ->
                if readyToActivate then
                  match neuronType with
                  | Actuator props ->
                    sprintf "Activating Actuator %A" nodeId |> infoLog
                    props |> activateActuator barrier
                    replyChannel.Reply ()
                    let notReadyToActivate = Some false
                    return! loop overflowBarrier inboundConnections outboundConnections maximumVectorLength notReadyToActivate recurrentOutboundConnections Map.empty
                  | _ ->
                    replyChannel.Reply ()
                    return! loop barrier inboundConnections outboundConnections maximumVectorLength maybeCortex recurrentOutboundConnections overflowBarrier
                else
                  replyChannel.Reply ()
                  return! loop barrier inboundConnections outboundConnections maximumVectorLength maybeCortex recurrentOutboundConnections overflowBarrier
            | GetNodeStatus (checkIfActuatorsAreReadyToActivate,replyChannel) ->
              let nodeStatus =
                match neuronType with
                | Actuator _ ->
                  match maybeCortex with
                  | None ->
                    if inbox.CurrentQueueLength = 0 then NodeIsReady else NodeIsBusy 
                  | Some readyToActivate ->
                    if inbox.CurrentQueueLength = 0 then
                      if checkIfActuatorsAreReadyToActivate then
                        if readyToActivate then NodeIsReady else NodeIsBusy
                      else
                        NodeIsReady
                    else
                      NodeIsBusy
                | _ ->
                    if inbox.CurrentQueueLength = 0 then NodeIsReady else NodeIsBusy 
              nodeStatus |> replyChannel.Reply
              return! loop barrier inboundConnections outboundConnections maximumVectorLength maybeCortex recurrentOutboundConnections overflowBarrier
            | ResetNeuron replyChannel ->
              let updatedInboundConnections =
                inboundConnections
                |> Array.map(fun connection -> { connection with Weight = connection.InitialWeight})
              let rec tossQueuedMessages () =
                if inbox.CurrentQueueLength = 0 then
                  ()
                else
                  inbox.TryReceive(250) |> Async.RunSynchronously |> ignore
                  tossQueuedMessages()
              tossQueuedMessages ()
              replyChannel.Reply()
              return! loop Map.empty updatedInboundConnections outboundConnections maximumVectorLength maybeCortex recurrentOutboundConnections Map.empty
            | SendRecurrentSignals replyChannel ->
              let sendRecurrentSignalFromConnection (connection : NeuronConnection) =
                (connection.NodeId, connection.Neuron) |> sendRecurrentSignal ActivateIfNeuronHasOneConnection connection.NeuronConnectionId
              recurrentOutboundConnections
              |> Array.iter sendRecurrentSignalFromConnection
              replyChannel.Reply ()
              return! loop barrier inboundConnections outboundConnections maximumVectorLength maybeCortex recurrentOutboundConnections overflowBarrier
      }
    loop Map.empty Array.empty Array.empty 0 None Array.empty Map.empty
  )

  //Add exception logging
  neuronInstance |> (fun x -> x.Error.Add(fun err -> sprintf "%A" err |> infoLog))

  (nodeId, (nodeLayer, neuronInstance))
