﻿namespace MBrace.SampleRuntime

open System
open System.Threading

open Nessos.Thespian

open MBrace.Core
open MBrace.Core.Internals
open MBrace.Runtime
open MBrace.Runtime.Utils.PerformanceMonitor

[<AutoSerializable(true)>]
type WorkerRef private (hostname : string, pid : int, processorCount : int) =
    static let localWorker = lazy(
        let hostname = System.Net.Dns.GetHostName()
        let pid = System.Diagnostics.Process.GetCurrentProcess().Id
        let pc = System.Environment.ProcessorCount
        new WorkerRef(hostname, pid, pc))

    let id = sprintf "mbrace://%s/pid:%d" hostname pid
    interface IWorkerRef with
        member __.Hostname = hostname
        member __.Id = id
        member __.Type = "sample runtime worker node"
        member __.ProcessorCount = processorCount
        member __.ProcessId = pid
        member __.CompareTo(other : obj) =
            match other with
            | :? WorkerRef as w -> compare id (w :> IWorkerRef).Id
            | _ -> invalidArg "other" "invalid comparand."

    override __.ToString() = id
    override __.Equals other = 
        match other with
        | :? WorkerRef as w -> id = (w :> IWorkerRef).Id
        | _ -> false

    override __.GetHashCode() = hash id

    static member LocalWorker = localWorker.Value

type private HeartbeatMonitorMsg = 
    | SendHeartbeat
    | CheckHeartbeat
    | Stop of IReplyChannel<unit>

and private WorkerMonitorMsg =
    | Subscribe of IWorkerRef * WorkerState * IReplyChannel<ActorRef<HeartbeatMonitorMsg> * TimeSpan>
    | UnSubscribe of IWorkerRef
    | DeclareState of IWorkerRef * WorkerState
    | DeclarePerformanceMetrics of IWorkerRef * NodePerformanceInfo
    | DeclareDead of IWorkerRef
    | IsAlive of IWorkerRef * IReplyChannel<bool>
    | GetAvailableWorkers of IReplyChannel<(IWorkerRef * WorkerState * NodePerformanceInfo) []>

type private WorkerInfo =
    {
        State : WorkerState
        Perf : NodePerformanceInfo
        HeartbeatMonitor : ActorRef<HeartbeatMonitorMsg>
    }

module private HeartbeatMonitor =
    let create (threshold : TimeSpan) (wmon : ActorRef<WorkerMonitorMsg>) (worker : IWorkerRef) =
        let cts = new CancellationTokenSource()
        let behaviour (self : Actor<HeartbeatMonitorMsg>) (lastRenew : DateTime) (msg : HeartbeatMonitorMsg) = async {
            match msg with
            | SendHeartbeat -> 
                return DateTime.Now
            | CheckHeartbeat ->
                if DateTime.Now - lastRenew > threshold then
                    wmon <-- DeclareDead worker
                return lastRenew
            | Stop ch -> 
                cts.Cancel()
                do! ch.Reply (())
                self.Stop ()
                return lastRenew
        }

        let aref =
            Actor.SelfStateful DateTime.Now behaviour
            |> Actor.Publish
            |> Actor.ref

        let rec poll () = async {
            aref <-- CheckHeartbeat
            do! Async.Sleep (int threshold.TotalMilliseconds / 5)
            return! poll()
        }

        Async.Start(poll(), cts.Token)
        aref

    let initHeartbeat (threshold : TimeSpan) (target : ActorRef<HeartbeatMonitorMsg>) = async {
        let cts = new CancellationTokenSource()
        let rec loop () = async {
            try target <-- SendHeartbeat with _ -> ()
            do! Async.Sleep(int threshold.TotalMilliseconds / 5)
            return! loop ()
        }

        Async.Start(loop(), cts.Token)
        return { new IDisposable with member __.Dispose() = cts.Cancel() }
    }


[<AutoSerializable(true)>]
type WorkerManager private (source : ActorRef<WorkerMonitorMsg>) =

    member __.Subscribe(worker : IWorkerRef, initial : WorkerState) = async {
        let! heartbeatMon,threshold = source <!- fun ch -> Subscribe(worker, initial, ch)
        return! HeartbeatMonitor.initHeartbeat threshold heartbeatMon
    }

    member __.DeclareState(worker : IWorkerRef, state : WorkerState) = async {
        return! source.AsyncPost <| DeclareState (worker, state)
    }

    member __.UnSubscribe(worker : IWorkerRef) = async {
        return! source.AsyncPost <| UnSubscribe worker
    }

    member __.GetAllWorkers() = async {
        return! source <!- GetAvailableWorkers
    }

    member __.IsAlive(worker : IWorkerRef) = async {
        return! source <!- fun ch -> IsAlive(worker, ch)
    }

    interface IWorkerManager with
        member x.DeclareWorkerState(worker: IWorkerRef, state: WorkerState): Async<unit> = 
            x.DeclareState(worker, state)
        
        member x.GetAvailableWorkers(): Async<(IWorkerRef * WorkerState * NodePerformanceInfo) []> = 
            x.GetAllWorkers()
        
        member x.IsValidTargetWorker(target: IWorkerRef): Async<bool> = async {
            match target with
            | :? WorkerRef -> return! x.IsAlive(target)
            | _ -> return false
        }
        
        member x.SubmitPerformanceMetrics(worker: IWorkerRef, perf: NodePerformanceInfo): Async<unit> = async {
            return! source <!- fun ch -> DeclarePerformanceMetrics(worker, perf)
        }
        
        member x.SubscribeWorker(worker: IWorkerRef, initial: WorkerState): Async<IDisposable> = async {
            let! heartbeatMon, threshold = source <!- fun ch -> Subscribe(worker, initial, ch)
            let! unsubscriber = HeartbeatMonitor.initHeartbeat threshold heartbeatMon
            return new WorkerSubscriptionManager(x, worker, unsubscriber) :> IDisposable
        }

    static member Init(?heartbeatThreshold : TimeSpan) =
        let heartbeatThreshold = defaultArg heartbeatThreshold (TimeSpan.FromSeconds 4.)
        let behaviour (self : Actor<WorkerMonitorMsg>) (state : Map<IWorkerRef, WorkerInfo>) (msg : WorkerMonitorMsg) = async {
            match msg with
            | Subscribe(w, ws, rc) ->
                let hmon = HeartbeatMonitor.create heartbeatThreshold self.Ref w
                do! rc.Reply(hmon, heartbeatThreshold)
                let info = { State = ws ; HeartbeatMonitor = hmon ; Perf = Unchecked.defaultof<_> }
                return (state.Add(w, info))

            | UnSubscribe w ->
                match state.TryFind w with
                | None -> return state
                | Some info -> 
                    do! info.HeartbeatMonitor <!- Stop
                    return state.Remove w
            
            | DeclareDead w ->
                match state.TryFind w with
                | None -> return state
                | Some { HeartbeatMonitor = hmon } ->
                    do! hmon <!- Stop
                    return state.Remove w

            | DeclareState (w, ws) ->
                match state.TryFind w with
                | None -> return state
                | Some info -> return state.Add(w, { info with State = ws })

            | DeclarePerformanceMetrics(w, perf) ->
                match state.TryFind w with
                | None -> return state
                | Some info -> return state.Add(w, { info with Perf = perf })

            | GetAvailableWorkers rc ->
                let workers = state |> Seq.map (fun kv -> kv.Key, kv.Value.State, kv.Value.Perf) |> Seq.toArray
                do! rc.Reply workers
                return state

            | IsAlive (w,rc) ->
                do! rc.Reply (state.ContainsKey w)
                return state
        }

        let aref =
            Actor.SelfStateful Map.empty behaviour
            |> Actor.Publish
            |> Actor.ref

        new WorkerManager(aref)



and [<AutoSerializable(false)>] private 
    WorkerSubscriptionManager (wmon : WorkerManager, currentWorker : IWorkerRef, heartbeatDisposer : IDisposable) =

    let mutable isDisposed = false
    let lockObj = obj()

    /// Unsubscribes worker from global worker monitor
    member __.UnSubscribe() =
        lock lockObj (fun () ->
            if isDisposed then () else
            heartbeatDisposer.Dispose()
            wmon.UnSubscribe currentWorker |> Async.RunSync
            isDisposed <- true)

    interface IDisposable with
        member __.Dispose() = __.UnSubscribe()