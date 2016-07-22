#define DEBUGFRONTVEHx
#define DEBUGREGx
#define DEBUGMETRICx

using System;
using System.Collections.Generic;
using System.Linq;
using ColossalFramework;
using TrafficManager.Traffic;
using TrafficManager.TrafficLight;
using TrafficManager.Custom.AI;
using TrafficManager.Util;
using System.Threading;
using TrafficManager.State;
using TrafficManager.UI;

/// <summary>
/// A segment end describes a directional traffic segment connected to a controlled node
/// (having custom traffic lights or priority signs).
/// </summary>
namespace TrafficManager.Traffic {
	public class SegmentEnd : IObserver<SegmentGeometry>, IObserver<NodeGeometry> {
		public enum PriorityType {
			None = 0,
			Main = 1,
			Stop = 2,
			Yield = 3
		}

		public ushort NodeId {
			get; private set;
		}

		public ushort SegmentId {
			get; private set;
		}

		public bool StartNode {
			get; private set;
		} = false;

		private int numLanes = 0;

		public PriorityType Type {
			get { return type; }
			set { type = value; Housekeeping(); }
		}
		private PriorityType type = PriorityType.None;

		private IDisposable segGeometryUnsubscriber;
		private IDisposable nodeGeometryUnsubscriber;

		/// <summary>
		/// Vehicles that are traversing or will traverse this segment
		/// </summary>
		internal ushort FirstRegisteredVehicleId = 0;

		private bool cleanupRequested = false;

		/// <summary>
		/// Vehicles that are traversing or will traverse this segment
		/// </summary>
		//private ushort[] frontVehicleIds;

		/// <summary>
		/// Number of vehicles / vehicle length goint to a certain segment
		/// </summary>
		private Dictionary<ushort, uint> numVehiclesFlowingToSegmentId; // minimum speed required
		private Dictionary<ushort, uint> numVehiclesGoingToSegmentId; // no minimum speed required

		public SegmentEnd(ushort nodeId, ushort segmentId, PriorityType type) {
			NodeId = nodeId;
			SegmentId = segmentId;
			Type = type;
			FirstRegisteredVehicleId = 0;
			SegmentGeometry segGeometry = SegmentGeometry.Get(segmentId);
			OnUpdate(segGeometry);
			segGeometryUnsubscriber = segGeometry.Subscribe(this);
			NodeGeometry nodeGeometry = NodeGeometry.Get(nodeId);
			OnUpdate(nodeGeometry);
			nodeGeometryUnsubscriber = nodeGeometry.Subscribe(this);
		}

		~SegmentEnd() {
			Destroy();
		}

		internal void RequestCleanup() {
			cleanupRequested = true;
		}

		internal void SimulationStep() {
			if (cleanupRequested) {
#if DEBUG
				//Log._Debug($"Cleanup of SegmentEnd {SegmentId} @ {NodeId} requested. Performing cleanup now.");
#endif
				ushort vehicleId = FirstRegisteredVehicleId;
				while (vehicleId != 0) {
					bool removeVehicle = false;
					if ((Singleton<VehicleManager>.instance.m_vehicles.m_buffer[vehicleId].m_flags & Vehicle.Flags.Created) == 0) {
						removeVehicle = true;
					} else {
						VehicleState state = VehicleStateManager.GetVehicleState(vehicleId);
						if (state == null) {
							removeVehicle = true;
						} else {
							if (!state.HasPath(ref Singleton<VehicleManager>.instance.m_vehicles.m_buffer[vehicleId])) {
								removeVehicle = true;
							}
						}
					}

					VehicleState nextState = VehicleStateManager._GetVehicleState(vehicleId);
					ushort nextVehicleId = nextState.NextVehicleIdOnSegment;
					if (removeVehicle) {
						nextState.Unlink();
					}
					vehicleId = nextVehicleId;
				}

				cleanupRequested = false;
			}

			Housekeeping();
		}

		/// <summary>
		/// Calculates for each segment the number of cars going to this segment.
		/// We use integer arithmetic for better performance.
		/// </summary>
		public Dictionary<ushort, uint> GetVehicleMetricGoingToSegment(bool includeStopped=true, byte? laneIndex=null, bool debug = false) {
			VehicleManager vehicleManager = Singleton<VehicleManager>.instance;
			NetManager netManager = Singleton<NetManager>.instance;

			Dictionary<ushort, uint> ret = includeStopped ? numVehiclesGoingToSegmentId : numVehiclesFlowingToSegmentId;

			for (var s = 0; s < 8; s++) {
				ushort segmentId = netManager.m_nodes.m_buffer[NodeId].GetSegment(s);

				if (segmentId == 0 || segmentId == SegmentId)
					continue;

				if (!ret.ContainsKey(segmentId))
					continue;

				ret[segmentId] = 0;
			}

#if DEBUGMETRIC
			if (debug)
				Log._Debug($"GetVehicleMetricGoingToSegment: Segment {SegmentId}, Node {NodeId}. Target segments: {string.Join(", ", ret.Keys.Select(x => x.ToString()).ToArray())}, Registered Vehicles: {string.Join(", ", GetRegisteredVehicles().Select(x => x.Key.ToString()).ToArray())}");
#endif

			ushort vehicleId = FirstRegisteredVehicleId;
			int numProcessed = 0;
			while (vehicleId != 0) {
				VehicleState state = VehicleStateManager._GetVehicleState(vehicleId);
				if (!state.Valid) {
					RequestCleanup();
					vehicleId = state.NextVehicleIdOnSegment;
					continue;
				}

				bool breakLoop = false;

				state.ProcessCurrentAndNextPathPosition(ref Singleton<VehicleManager>.instance.m_vehicles.m_buffer[vehicleId], delegate (ref PathUnit.Position curPos, ref PathUnit.Position nextPos) {
#if DEBUGMETRIC
				if (debug)
					Log._Debug($" GetVehicleMetricGoingToSegment: Checking vehicle {vehicleId}");
#endif

					if (!ret.ContainsKey(nextPos.m_segment)) {
#if DEBUGMETRIC
					if (debug)
						Log._Debug($"  GetVehicleMetricGoingToSegment: ret does not contain key for target segment {pos.TargetSegmentId}");
#endif
						return;
					}

					if (!includeStopped && vehicleManager.m_vehicles.m_buffer[vehicleId].GetLastFrameVelocity().magnitude < TrafficPriority.maxStopVelocity) {
#if DEBUGMETRIC
					if (debug)
						Log._Debug($"  GetVehicleMetricGoingToSegment: Vehicle {vehicleId}: too slow");
#endif
						++numProcessed;
						return;
					}

					if (laneIndex != null && curPos.m_lane != laneIndex) {
#if DEBUGMETRIC
					if (debug)
						Log._Debug($"  GetVehicleMetricGoingToSegment: Vehicle {vehicleId}: Lane index mismatch (expected: {laneIndex}, was: {pos.SourceLaneIndex})");
#endif
						return;
					}

					if (Options.simAccuracy <= 2) {
						uint avgSegmentLength = (uint)netManager.m_segments.m_buffer[SegmentId].m_averageLength;
						uint normLength = Math.Min(100u, (uint)(state.TotalLength * 100u) / avgSegmentLength);

#if DEBUGMETRIC
						if (debug)
							Log._Debug($"  GetVehicleMetricGoingToSegment: NormLength of vehicle {vehicleId}: {avgSegmentLength} -> {normLength}");
#endif

#if DEBUGMETRIC
						if (debug)
							ret[pos.TargetSegmentId] += 1;
						else
							ret[pos.TargetSegmentId] = Math.Min(100u, ret[pos.TargetSegmentId] + normLength);
#else
							ret[nextPos.m_segment] += normLength;
#endif
					} else {
						ret[nextPos.m_segment] += 10;
					}
					++numProcessed;

					if ((Options.simAccuracy >= 3 && numProcessed >= 3) || (Options.simAccuracy == 2 && numProcessed >= 5) || (Options.simAccuracy == 1 && numProcessed >= 10)) {
						breakLoop = true;
						return;
					}

#if DEBUGMETRIC
				if (debug)
					Log._Debug($"  GetVehicleMetricGoingToSegment: Vehicle {vehicleId}: *added*! Coming from segment {SegmentId}, lane {laneIndex}. Going to segment {pos.TargetSegmentId}, lane {pos.TargetLaneIndex}, minSpeed={TrafficPriority.maxStopVelocity}, speed={speed}");
#endif
				});

				if (breakLoop)
					break;

				vehicleId = state.NextVehicleIdOnSegment;
			}

#if DEBUGMETRIC
			if (debug)
				Log._Debug($"GetVehicleMetricGoingToSegment: Calculation completed. {string.Join(", ", ret.Select(x => x.Key.ToString() + "=" + x.Value.ToString()).ToArray())}");
#endif
			return ret;
		}

		internal int GetRegisteredVehicleCount() {
			ushort vehicleId = FirstRegisteredVehicleId;
			int ret = 0;
			while (vehicleId != 0) {
				++ret;
				vehicleId = VehicleStateManager._GetVehicleState(vehicleId).NextVehicleIdOnSegment;
			}
			return ret;
		}

		internal void Destroy() {
			UnregisterAllVehicles();
			if (segGeometryUnsubscriber != null)
				segGeometryUnsubscriber.Dispose();
			if (nodeGeometryUnsubscriber != null)
				nodeGeometryUnsubscriber.Dispose();
		}

		private void UnregisterAllVehicles() {
			while (FirstRegisteredVehicleId != 0) {
				VehicleStateManager._GetVehicleState(FirstRegisteredVehicleId).Unlink();
			}
		}

		public void OnUpdate(SegmentGeometry geometry) {
			if (!geometry.IsValid()) {
				TrafficPriority.RemovePrioritySegment(NodeId, SegmentId);
				return;
			}

			StartNode = Singleton<NetManager>.instance.m_segments.m_buffer[SegmentId].m_startNode == NodeId;
			numLanes = Singleton<NetManager>.instance.m_segments.m_buffer[SegmentId].Info.m_lanes.Length;
			numVehiclesFlowingToSegmentId = new Dictionary<ushort, uint>(7);
			numVehiclesGoingToSegmentId = new Dictionary<ushort, uint>(7);
			//frontVehicleIds = new ushort[numLanes];
			ushort[] outgoingSegmentIds = geometry.GetOutgoingSegments(StartNode);
			foreach (ushort otherSegmentId in outgoingSegmentIds) {
				numVehiclesFlowingToSegmentId[otherSegmentId] = 0;
				numVehiclesGoingToSegmentId[otherSegmentId] = 0;
			}
		}

		public void OnUpdate(NodeGeometry geometry) {
			Housekeeping();
		}

		internal void Housekeeping() {
			if (TrafficManagerTool.GetToolMode() != ToolMode.AddPrioritySigns && TrafficLightSimulation.GetNodeSimulation(NodeId) == null && Type == PriorityType.None)
				TrafficPriority.RemovePrioritySegments(NodeId);
		}
	}
}
