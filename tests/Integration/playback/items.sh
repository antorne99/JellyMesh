source "$(dirname "$0")/lib.sh"
login admin
get /JellyMesh/Admin/Sync | jq -c '{IsRunning,Health,Servers:[.Servers[]|{ServerName,IsOffline,Movies,Episodes}]}'
get '/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path,MediaSources' | jq -r '.Items[]|[.Id,.Name,(.RunTimeTicks//0|tostring),(.Path|split("/")|.[1]),(.MediaSources[0].Path//"")]|join(" | ")'
docker exec $NAME cat /config/data/plugins/configurations/x 2>/dev/null
docker exec $NAME find /config/plugins -name 'federated-stubs.json'
