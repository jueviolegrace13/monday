#!/usr/bin/env bash
# Starts Monday locally (watching itself), sends mixed traffic, and checks stats, replay, user replay,
# the live stream, and that nothing private (query values, keys, user names) was stored.
# Build first: dotnet build
set -u
S=${TMPDIR:-/tmp}/monday-check; mkdir -p $S
cd "$(dirname "$0")/.."
# Optional: CONNECTION="Host=...;Database=..." to run against Postgres instead of memory.
pkill -f '^dotnet src/Monday.Api' 2>/dev/null; sleep 1
ConnectionStrings__Monday="${CONNECTION:-}" ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://127.0.0.1:5077 Monday__Bootstrap__Owner=github:rendee \
  nohup dotnet src/Monday.Api/bin/Debug/net10.0/Monday.Api.dll > $S/api2.log 2>&1 &
sleep 5
grep -i "project Monday" $S/api2.log | head -1
B=http://127.0.0.1:5077; U="X-Monday-Dev-User: github:rendee"
PID=$(curl -s $B/projects -H "$U" | python3 -c 'import sys,json;print(json.load(sys.stdin)[0]["id"])'); echo "project: $PID"
curl -sN $B/projects/$PID/traffic/live -H "$U" > $S/sse.txt &
SSE=$!
sleep 1
for i in $(seq 1 20); do
  curl -s -o /dev/null $B/health
  curl -s -o /dev/null $B/projects -H "$U"
  curl -s -o /dev/null "$B/projects/$PID/graph?secret=abc" -H "X-Monday-Dev-User: github:ana"
  curl -s -o /dev/null -X POST $B/ingest/graph -H "X-Monday-Key: mon_bad_key" -H 'content-type: application/json' -d '{}'
done
curl -s -o /dev/null $B/nope/here
sleep 4
echo "stats: $(curl -s "$B/projects/$PID/traffic/stats?seconds=120" -H "$U")"
SINCE=$(date -u -d '-5 min' +%Y-%m-%dT%H:%M:%SZ); UNTIL=$(date -u -d '+1 min' +%Y-%m-%dT%H:%M:%SZ)
curl -s "$B/projects/$PID/traffic?since=$SINCE&until=$UNTIL" -H "$U" > $S/replay.json
python3 - $S/replay.json <<'EOF'
import sys,json; from collections import Counter
d=json.load(open(sys.argv[1])); raw=json.dumps(d)
print("replay:",len(d))
for k,v in Counter((x["method"]+" "+x["route"],x["status"],x["outcome"]) for x in d).most_common(7): print("  ",v,k)
print("leaks:",[s for s in ["secret=abc","mon_bad","github:","ana","rendee","nope/here"] if s in raw] or "none")
EOF
echo "user replay (rendee): $(curl -s "$B/projects/$PID/traffic?since=$SINCE&until=$UNTIL&userId=github:rendee" -H "$U" | python3 -c 'import sys,json;d=json.load(sys.stdin);print(len(d), sorted(set(x["route"] for x in d)))')"
kill $SSE 2>/dev/null
echo "live stream events: $(grep -c '^event: request' $S/sse.txt)"
