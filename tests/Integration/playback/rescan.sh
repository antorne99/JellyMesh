source "$(dirname "$0")/lib.sh"
login admin
IT=$(get "/Items?Recursive=true&IncludeItemTypes=Movie&SearchTerm=JM%20Audio" | jq -r '.Items[0].Id')
post "/Items/$IT/Refresh?metadataRefreshMode=FullRefresh&replaceAllMetadata=false" ''
post /Library/Refresh; sleep 15
get "/Items/$IT?Fields=MediaStreams" | jq -c '[.MediaStreams[]|{Index,Type,IsExternal,Path}]'
