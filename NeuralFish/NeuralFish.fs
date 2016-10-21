module NeuralFish.Core

open NeuralFish.Types

let mutable InfoLogging = true
let infoLog (message : string) =
  if (InfoLogging) then
    System.Console.WriteLine(message)

let killNeuralNetwork (liveNeurons : NeuralNetwork) =
  let rec waitOnNeuralNetwork neuralNetworkToWaitOn : NeuralNetwork =
    let checkIfNeuralNetworkIsActive (neuralNetwork : NeuralNetwork) =
      //returns true if active
      neuralNetwork
      |> Map.forall(fun i neuron -> neuron.CurrentQueueLength <> 0)
    if neuralNetworkToWaitOn |> checkIfNeuralNetworkIsActive then
      //200 milliseconds of sleep seems plenty while waiting on the NN
      System.Threading.Thread.Sleep(200)
      waitOnNeuralNetwork neuralNetworkToWaitOn
    else
      neuralNetworkToWaitOn
  let killNeuralNetwork (neuralNetworkToKill : NeuralNetwork) =
    neuralNetworkToKill
    |> Map.toArray
    |> Array.Parallel.iter(fun (_,neuron) -> Die |> neuron.PostAndReply)

  liveNeurons
  |> waitOnNeuralNetwork
  |> killNeuralNetwork

let synchronize (_, (sensor : NeuronInstance)) =
  Sync |> sensor.Post

let synapseDotProduct synapses =
  let rec loop synapses =
    match synapses with
    | [] -> 0.0
    | (_,value,weight)::tail -> value*weight + (loop tail)
  synapses |> Map.toList |> List.map snd |> loop

let createNeuron id activationFunction activationFunctionId bias =
  let record =
    {
      NodeId = id
      NodeType = NodeRecordType.Neuron
      OutboundConnections = Map.empty
      Bias = Some bias
      ActivationFunctionId = Some activationFunctionId
      SyncFunctionId = None
      OutputHookId = None
    }
  {
    Record = record
    ActivationFunction = activationFunction
  } |> Neuron
let createSensor id syncFunction syncFunctionId =
  let record =
    {
      NodeId = id
      NodeType = NodeRecordType.Sensor
      OutboundConnections = Map.empty
      Bias = None
      ActivationFunctionId = None
      SyncFunctionId = Some syncFunctionId
      OutputHookId = None
    }
  {
    Record = record
    SyncFunction = syncFunction
  } |> Sensor
let createActuator id outputHook outputHookId =
  let record =
    {
      NodeId = id
      NodeType = NodeRecordType.Actuator
      OutboundConnections = Map.empty
      Bias = None
      ActivationFunctionId = None
      SyncFunctionId = None
      OutputHookId = Some outputHookId
    }
  {
    Record = record
    OutputHook = outputHook
  } |> Actuator

let connectNodeToNeuron (toNodeId, toNode) weight (fromNodeId, (fromNode : NeuronInstance)) =
  (fun r -> ((toNode,toNodeId,weight),r) |> NeuronActions.AddOutboundConnection)
  |> fromNode.PostAndReply

let connectNodeToActuator actuator fromNode  =
    connectNodeToNeuron actuator 0.0 fromNode

let connectSensorToNode toNode weights sensor =
 let createConnectionsFromWeight toNode fromNode weight =
   sensor |> connectNodeToNeuron toNode weight
 weights |> Seq.iter (sensor |> createConnectionsFromWeight toNode )

let createNeuronInstance neuronType =
  let getNodeIdFromProps neuronType =
    match neuronType with
      | Neuron props ->
        props.Record.NodeId
      | Sensor props ->
        props.Record.NodeId
      | Actuator props ->
        props.Record.NodeId
  let isBarrierSatisifed (inboundNeuronConnections : InboundNeuronConnections) (barrier : IncomingSynapses) =
    inboundNeuronConnections
    |> Seq.forall(fun connectionId -> barrier |> Map.containsKey connectionId)
  let sendSynapseToNeurons (outputNeurons : NeuronConnections) outputValue =
    let sendSynapseToNeuron outputValue neuronConnectionId outputNeuronConnection =
      (neuronConnectionId, (outputNeuronConnection.NodeId, outputValue, outputNeuronConnection.Weight))
      |> ReceiveInput
      |> outputNeuronConnection.Neuron.Post
    outputNeurons
    |> Map.iter (sendSynapseToNeuron outputValue)
  let addBias bias outputVal =
    outputVal + bias
  let activateNeuron (barrier : IncomingSynapses) (outboundConnections : NeuronConnections) neuronType =
    match neuronType with
    | Neuron props ->
      let logNeuronOutput nodeId activationFunctionId bias outputValue =
        sprintf "Neuron %A is outputting %A after activation %A and bias %A" nodeId outputValue activationFunctionId bias
        |> infoLog
        outputValue

      let someBias =
        match props.Record.Bias with
        | Some bias -> bias
        | None ->
          raise (NoBiasInRecordForNeuronException <| sprintf "Neuron %A does not have a bias" props.Record.NodeId)
      barrier
      |> synapseDotProduct
      |> addBias someBias
      |> props.ActivationFunction
      |> logNeuronOutput props.Record.NodeId props.Record.ActivationFunctionId props.Record.Bias
      |> sendSynapseToNeurons outboundConnections
    | Actuator props ->
      let logActuatorOutput nodeId outputHookId outputValue =
        sprintf "Actuator %A is outputting %A with output hook %A" nodeId outputValue outputHookId
        |> infoLog
        outputValue
      barrier
      |> Map.toSeq
      |> Seq.sumBy (fun (_,(_,value,weight)) -> value)
      |> logActuatorOutput props.Record.NodeId props.Record.OutputHookId
      |> props.OutputHook
    | Sensor _ ->
      ()

  let createInactiveNeuronConnection activeNeuronConnection =
    activeNeuronConnection.NodeId, activeNeuronConnection.Weight

  let neuronInstance = NeuronInstance.Start(fun inbox ->
    let rec loop barrier (inboundConnections : InboundNeuronConnections) (outboundConnections : NeuronConnections) =
      async {
        let! someMsg = inbox.TryReceive 20000
        match someMsg with
        | None ->
          "Neuron did not receive message in 20 seconds. Looping mailbox" |> infoLog
          return! loop barrier inboundConnections outboundConnections
        | Some msg ->
          match msg with
          | Sync ->
            match neuronType with
            | Neuron _ ->
              return! loop barrier inboundConnections outboundConnections
            | Actuator _ ->
              return! loop barrier inboundConnections outboundConnections
            | Sensor props ->
              let rec processSensorSync dataStream connectionId connection =
                let sendSynapseToNeuron (neuron : NeuronInstance) neuronConnectionId weight outputValue =
                  (neuronConnectionId, (props.Record.NodeId, outputValue, weight))
                  |> ReceiveInput
                  |> neuron.Post

                let data = dataStream |> Seq.head
                sprintf "Sending %A to connection %A with a weight of %A" data connectionId connection.Weight |> infoLog
                data |> sendSynapseToNeuron connection.Neuron connectionId connection.Weight
              outboundConnections
              |> Map.iter (processSensorSync <| props.SyncFunction())
              return! loop barrier inboundConnections outboundConnections
          | ReceiveInput (neuronConnectionId, package) ->
            let updatedBarrier : IncomingSynapses =
              barrier
              |> Map.add neuronConnectionId package
            match neuronType with
            | Neuron props ->
              if updatedBarrier |> isBarrierSatisifed inboundConnections then
                sprintf "Barrier is satisifed for Node %A" props.Record.NodeId |> infoLog
                neuronType |> activateNeuron updatedBarrier outboundConnections
                return! loop Map.empty inboundConnections outboundConnections
              else
                sprintf "Node %A not activated. Received %A from %A" props.Record.NodeId package neuronConnectionId |> infoLog
                return! loop updatedBarrier inboundConnections outboundConnections
            | Actuator props ->
              if updatedBarrier |> isBarrierSatisifed inboundConnections then
                sprintf "Barrier is satisifed for Node %A" props.Record.NodeId |> infoLog
                neuronType |> activateNeuron updatedBarrier outboundConnections
                return! loop Map.empty inboundConnections outboundConnections
              else
                sprintf "Node %A not activated. Received %A from %A" props.Record.NodeId package neuronConnectionId |> infoLog
                return! loop updatedBarrier inboundConnections outboundConnections
            | Sensor _ ->
              //Sensors use the sync msg
              return! loop Map.empty inboundConnections outboundConnections
          | AddOutboundConnection ((toNode,nodeId,weight),replyChannel) ->
              let neuronConnectionId = System.Guid.NewGuid()
              let updatedOutboundConnections =
                let outboundConnection =
                 {
                  NodeId = nodeId
                  Neuron = toNode
                  Weight = weight
                 }
                outboundConnections |> Map.add neuronConnectionId outboundConnection

              (neuronConnectionId, replyChannel)
              |> AddInboundConnection
              |> toNode.Post

              sprintf "Node %A is adding Node %A as an outbound connection %A with weight %A" neuronType nodeId neuronConnectionId weight
              |> infoLog
              return! loop barrier inboundConnections updatedOutboundConnections
            | AddInboundConnection (neuronConnectionId,replyChannel) ->
              let updatedInboundConnections =
                inboundConnections |> Seq.append(Seq.singleton neuronConnectionId)
              replyChannel.Reply()
              sprintf "Added inbound neuron connection %A" neuronConnectionId
              |> infoLog
              return! loop barrier updatedInboundConnections outboundConnections
            | GetNodeRecord replyChannel ->
              let getOutboundNodeRecordConnections () : NodeRecordConnections =
                outboundConnections
                |> Map.map (fun neuronConnectionId neuronConnection -> neuronConnection |> createInactiveNeuronConnection)
              let nodeRecord =
                match neuronType with
                | Neuron props ->
                  let outboundNodeRecordConnections = getOutboundNodeRecordConnections ()
                  { props.Record with OutboundConnections = outboundNodeRecordConnections }
                | Sensor props ->
                  let outboundNodeRecordConnections = getOutboundNodeRecordConnections ()
                  { props.Record with OutboundConnections = outboundNodeRecordConnections }
                | Actuator props -> props.Record
              nodeRecord |> replyChannel.Reply
              return! loop barrier inboundConnections outboundConnections
            | Die replyChannel ->
              replyChannel.Reply()
              return! loop barrier inboundConnections outboundConnections

      }
    loop Map.empty Seq.empty Map.empty
  )

  (neuronType |> getNodeIdFromProps, neuronInstance)
