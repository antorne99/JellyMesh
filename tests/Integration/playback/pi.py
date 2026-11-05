import re, json, sys
b = open(sys.argv[1], 'rb').read()
for m in re.finditer(rb'\{"MediaSources":\[', b):
    o, _ = json.JSONDecoder().raw_decode(b[m.start():].decode('utf8', 'ignore'))
    s = o['MediaSources'][0]
    print({k: s.get(k) for k in ('Id', 'Path', 'Protocol', 'DefaultAudioStreamIndex', 'DefaultSubtitleStreamIndex', 'TranscodingContainer', 'ETag', 'Name')}, o['PlaySessionId'])
    print(s['TranscodingUrl'][:170])
    print([(x['Index'], x['Type'], x.get('DeliveryMethod'), (x.get('DeliveryUrl') or '')[:80], x.get('Path')) for x in s['MediaStreams']])
