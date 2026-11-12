using System.Collections.Generic;
using System.Linq;
using System.Text;
using CbreTwin.Net;
using UnityEngine;
using Component = CbreTwin.Net.Component;

namespace CbreTwin
{
    public class TourStop
    {
        public uint RoomId;
        public int Floor;
        public string Title;
        public string Subtitle;
        public string Narration;
        public Vector3 Stand;          // feet position (metres)
        public Vector3 LookAt;         // metres
        public List<Vector3> Route = new(); // waypoints from the previous stop (metres)
        public bool Teleport;          // no walkable connection: fade across
        public string Severity = "ok"; // worst structural finding in the room
    }

    /// <summary>
    /// Turns the plan into a guided walkthrough: visits each meaningful room level by level,
    /// routing through real doorways, and writes a short narration from the twin's data.
    /// </summary>
    public static class TourPlanner
    {
        static readonly HashSet<string> PassThrough = new() { "hall", "stair", "closet", "pantry", "mudroom", "pool" };

        public static List<TourStop> Plan(BlueprintBuilder b, TwinConnection net, uint bpId)
        {
            var stops = new List<TourStop>();
            var rooms = b.RoomsById.Values.ToList();
            var adj = new Dictionary<uint, List<(uint to, Vector3 door)>>();
            foreach (var r in rooms) adj[r.Id] = new List<(uint, Vector3)>();
            foreach (var d in b.Doorways)
            {
                if (!adj.ContainsKey(d.A) || !adj.ContainsKey(d.B)) continue;
                adj[d.A].Add((d.B, d.PointFt));
                adj[d.B].Add((d.A, d.PointFt));
            }

            var findings = net.Findings(bpId);
            var comps = net.Components(bpId);
            TourStop prev = null;

            for (int f = 0; f < b.FloorCount; f++)
            {
                var floorRooms = rooms.Where(r => r.Floor == f).ToList();
                if (floorRooms.Count == 0) continue;
                var start = floorRooms.FirstOrDefault(r => r.Kind == "living")
                            ?? floorRooms.FirstOrDefault(r => r.Kind == "stair")
                            ?? floorRooms.OrderByDescending(r => r.W * r.D).First();

                // Depth-first visit order following doorways; unreachable rooms appended last.
                var order = new List<Room>();
                var seen = new HashSet<uint>();
                void Visit(Room r)
                {
                    if (!seen.Add(r.Id)) return;
                    order.Add(r);
                    foreach (var (to, _) in adj[r.Id].OrderBy(n => PassThrough.Contains(b.RoomsById[n.to].Kind) ? 1 : 0))
                        if (b.RoomsById[to].Floor == f) Visit(b.RoomsById[to]);
                }
                Visit(start);
                foreach (var r in floorRooms.OrderByDescending(r => r.W * r.D)) Visit(r);

                foreach (var r in order.Where(r => !PassThrough.Contains(r.Kind) && r.W * r.D >= 40))
                {
                    var stop = new TourStop { RoomId = r.Id, Floor = f };
                    var center = CenterFt(r, b);
                    List<uint> path = prev != null && prev.Floor == f ? Bfs(adj, prev.RoomId, r.Id) : null;
                    Vector3 entry;
                    if (path != null && path.Count >= 2)
                    {
                        for (int i = 0; i < path.Count - 1; i++)
                        {
                            var ra = b.RoomsById[path[i]];
                            var rb = b.RoomsById[path[i + 1]];
                            var door = adj[ra.Id].First(n => n.to == rb.Id).door;
                            var (before, after) = DoorApproach(ra, rb, door, b);
                            if (i > 0) stop.Route.Add(ToM(CenterFt(ra, b)));
                            stop.Route.Add(ToM(before));
                            stop.Route.Add(ToM(after));
                        }
                        var lastDoor = adj[path[^2]].First(n => n.to == r.Id).door;
                        entry = new Vector3(lastDoor.x, center.y, lastDoor.z);
                    }
                    else
                    {
                        stop.Teleport = prev != null;
                        // stand near the side of the room facing its longest view
                        entry = r.W >= r.D ? new Vector3((float)r.X + 1.5f, center.y, center.z) : new Vector3(center.x, center.y, (float)r.Z + 1.5f);
                    }
                    var stand = Vector3.Lerp(entry, center, r.Kind == "garage" ? 0.35f : 0.5f);
                    stand = ClampInside(r, stand, 1.4f);
                    var dir = (center - entry); dir.y = 0;
                    if (dir.sqrMagnitude < 0.01f) dir = Vector3.forward;
                    stop.Stand = ToM(stand);
                    stop.LookAt = ToM(center + dir.normalized * Mathf.Max(2f, (float)Mathf.Min((float)r.W, (float)r.D) * 0.4f) + Vector3.up * 4.5f);
                    stop.Route.Add(stop.Stand);
                    Describe(stop, r, findings, comps, net);
                    stops.Add(stop);
                    prev = stop;
                }
            }
            return stops;
        }

        static Vector3 CenterFt(Room r, BlueprintBuilder b) => new((float)(r.X + r.W / 2), b.LevelBaseFt((int)r.Floor), (float)(r.Z + r.D / 2));
        static Vector3 ToM(Vector3 ft) => ft * BlueprintBuilder.FT;

        static Vector3 ClampInside(Room r, Vector3 p, float margin) => new(
            Mathf.Clamp(p.x, (float)r.X + margin, (float)(r.X + r.W) - margin), p.y,
            Mathf.Clamp(p.z, (float)r.Z + margin, (float)(r.Z + r.D) - margin));

        /// <summary>Points just either side of a doorway, perpendicular to its wall.</summary>
        static (Vector3 before, Vector3 after) DoorApproach(Room a, Room bRoom, Vector3 door, BlueprintBuilder b)
        {
            bool onZEdge = Mathf.Abs(door.z - (float)a.Z) < 0.6f || Mathf.Abs(door.z - (float)(a.Z + a.D)) < 0.6f;
            var ca = CenterFt(a, b);
            var normal = onZEdge ? new Vector3(0, 0, Mathf.Sign(ca.z - door.z)) : new Vector3(Mathf.Sign(ca.x - door.x), 0, 0);
            var y = b.LevelBaseFt((int)a.Floor);
            var d = new Vector3(door.x, y, door.z);
            return (d + normal * 1.6f, d - normal * 1.6f);
        }

        static List<uint> Bfs(Dictionary<uint, List<(uint to, Vector3 door)>> adj, uint from, uint to)
        {
            var prev = new Dictionary<uint, uint>();
            var q = new Queue<uint>();
            q.Enqueue(from);
            var seen = new HashSet<uint> { from };
            while (q.Count > 0)
            {
                var n = q.Dequeue();
                if (n == to) break;
                foreach (var (m, _) in adj[n])
                    if (seen.Add(m)) { prev[m] = n; q.Enqueue(m); }
            }
            if (!seen.Contains(to)) return null;
            var path = new List<uint> { to };
            while (path[^1] != from) path.Add(prev[path[^1]]);
            path.Reverse();
            return path;
        }

        static void Describe(TourStop s, Room r, List<StructuralFinding> findings, List<Component> comps, TwinConnection net)
        {
            s.Title = r.Name;
            s.Subtitle = $"Level {r.Floor + 1}  ·  {r.W:0}' × {r.D:0}'  ·  {r.W * r.D:#,0} sq ft  ·  {r.Ceiling:0}' ceiling";
            var sb = new StringBuilder();
            var own = findings.Where(f => f.RoomId == r.Id && f.Severity != "ok")
                              .OrderByDescending(f => f.Severity == "critical" ? 2 : 1).ToList();
            if (own.Count > 0)
            {
                s.Severity = own[0].Severity;
                sb.Append(own[0].Severity == "critical" ? "Structural concern. " : "Worth a closer look. ");
                sb.Append(own[0].Detail);
                if (own.Count > 1) sb.Append($" ({own.Count - 1} more note{(own.Count > 2 ? "s" : "")} in this room.)");
            }
            else sb.Append(Ambient(r));

            var here = comps.Where(c => c.RoomId == r.Id).ToList();
            foreach (var c in here.Take(2))
            {
                var risk = net.Risk(c.Id);
                if (risk == null) continue;
                sb.Append($"\n\n{c.Name}: {c.Material.ToLowerInvariant()}. {c.SensorLabel} reads {c.SensorValue:0.#} {c.SensorUnit}; ");
                sb.Append($"{risk.CascadeProb * 100:0}% chance of failure in the next 12 months");
                sb.Append(risk.PreventionValue > 1000 ? $", and addressing it now avoids about {Palette.Money(risk.PreventionValue)} in knock-on damage." : ".");
            }
            s.Narration = sb.ToString();
        }

        static string Ambient(Room r) => r.Kind switch
        {
            "living" => r.Glazing >= 0.5 ? "The main living space, with a largely glazed exterior wall that pulls in afternoon light." : "The main living space and the natural centre of the house.",
            "kitchen" => r.Flags.Contains("island") ? "Working kitchen built around a central island." : "Working kitchen with perimeter counters.",
            "dining" => "Dining area adjoining the kitchen.",
            "primary" => "Primary bedroom suite.",
            "bedroom" => "Secondary bedroom with an exterior window for egress.",
            "bath" => "Bathroom. Wet rooms are where hidden moisture shows up first.",
            "office" => "A room that closes off — buyers treat it as a home office.",
            "game" => "Bonus room over the main level.",
            "media" => "Media room, insulated from the bedrooms.",
            "garage" => "Garage and service entry. The panel and water heater usually live here.",
            "laundry" => "Laundry and mechanical space.",
            "patio" => "Covered outdoor living.",
            "deck" => "Outdoor deck.",
            _ => "",
        };
    }
}
