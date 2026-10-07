#!/usr/bin/env bash
# Pushes a node into the fault band, shows the investigation that opens, and gives verdicts on it.
#
#   scripts/demo-fault.sh
#   API=http://localhost:5266/api NODE=B8:27:EB:10:00:03 scripts/demo-fault.sh
#
# Rack Row 3's temperature sensor is registered for 18 to 32. Readings of 999 are refused and each one counts against it, so a
# few hundred push a node that has been healthy for a while into fault: it keeps its last 256 outcomes and is in fault when the failures outweigh the good ones. The trust engine then raises FaultEntered, which opens an
# investigation: the score, the reason, the window from when the node was last nominal to the crossing, the alerts, the readings
# and what operators did the last time a node like it faulted.
#
# Then Dismiss (a false alarm: the node becomes slightly less sensitive to what caused it, within a floor), Confirm instead
# (which gives the sensitivity back), and a Relabel without a cause (refused).
set -u

API=${API:-http://localhost:5266/api}
NODE=${NODE:-B8:27:EB:10:00:03}
JSON='Content-Type: application/json'

show() {
  node -e '
    let text = "";
    process.stdin.on("data", chunk => text += chunk).on("end", () => {
      try {
        const j = JSON.parse(text);
        console.log(JSON.stringify('"$1"', null, 1));
      } catch { console.log(text.slice(0, 400)); }
    });'
}

echo "Trust before:"
curl -s "$API/trust/$NODE" | show '{ band: j.band, score: j.score, reason: j.reason }'

echo "Sending 300 readings the registration refuses (the node keeps its last 256 outcomes, so it takes enough to push the good ones out)."
for _ in $(seq 1 300); do
  curl -s -o /dev/null -X POST "$API/telemetry" -H "$JSON" -d "{\"macAddress\":\"$NODE\",\"value\":999}"
done

echo "Trust after:"
curl -s "$API/trust/$NODE" | show '{ band: j.band, score: j.score, reason: j.reason }'

ID=$(curl -s "$API/investigations?status=Open" | node -e '
  let text = "";
  process.stdin.on("data", chunk => text += chunk).on("end", () => {
    const found = JSON.parse(text).find(i => i.macAddress === process.argv[1]);
    console.log(found ? found.id : "");
  });' "$NODE")

if [ -z "$ID" ]; then
  echo "No open investigation for $NODE. Is the node already faulted from an earlier run? Try another NODE."
  exit 1
fi

echo
echo "The investigation that opened:"
curl -s "$API/investigations/$ID" | show '{
  id: j.id, room: j.room, status: j.status, score: j.score, reason: j.reason, causedBy: j.causedBy,
  window: [j.windowStart, j.windowEnd], activeAlerts: j.activeAlerts, trustSamples: j.trustHistory.length,
  readingCount: j.readingCount, precedent: j.precedent.answeredBy, sensitivity: j.sensitivity }'

echo
echo "Dismiss (a false alarm):"
curl -s -X POST "$API/investigations/$ID/verdict" -H "$JSON" -d '{"verdict":"Dismiss"}' \
  | show '{ status: j.status, verdict: j.verdict, change: j.sensitivityChange, sensitivity: j.sensitivity }'
echo "Trust after dismissing:"
curl -s "$API/trust/$NODE" | show '{ band: j.band, score: j.score }'

echo
echo "Changed to Confirm (the sensitivity is given back):"
curl -s -X POST "$API/investigations/$ID/verdict" -H "$JSON" -d '{"verdict":"Confirm"}' \
  | show '{ status: j.status, verdict: j.verdict, change: j.sensitivityChange, sensitivity: j.sensitivity }'
echo "Trust after confirming:"
curl -s "$API/trust/$NODE" | show '{ band: j.band, score: j.score }'

echo
echo "A relabel without a cause is refused:"
curl -s -w ' [HTTP %{http_code}]\n' -X POST "$API/investigations/$ID/verdict" -H "$JSON" -d '{"verdict":"Relabel"}' | head -c 500
echo
echo "A relabel with one is stored:"
curl -s -X POST "$API/investigations/$ID/verdict" -H "$JSON" -d '{"verdict":"Relabel","cause":"Power"}' \
  | show '{ status: j.status, verdict: j.verdict, cause: j.cause, change: j.sensitivityChange }'
