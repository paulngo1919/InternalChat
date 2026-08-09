# CORS, included by every API location.
#
# Constitution, Application Controls: "CORS MUST use an explicit origin
# allow-list. Wildcard origins are prohibited."
#
# $cors_origin comes from the map in nginx.conf and is EMPTY for any origin not
# on the list. Emitting an empty Access-Control-Allow-Origin is not the same as
# emitting `*` — the browser rejects the response, which is the intent.

add_header Access-Control-Allow-Origin      $cors_origin always;
add_header Access-Control-Allow-Credentials "true" always;
add_header Vary                             "Origin" always;

# Preflight is answered here rather than in the application: a 204 from the edge
# keeps an OPTIONS storm off the API's request threads.
if ($request_method = OPTIONS) {
    add_header Access-Control-Allow-Origin  $cors_origin always;
    add_header Access-Control-Allow-Methods "GET, POST, PATCH, DELETE, OPTIONS" always;
    add_header Access-Control-Allow-Headers "Authorization, Content-Type, X-Requested-With" always;
    add_header Access-Control-Max-Age       "600" always;
    add_header Content-Length               "0" always;
    return 204;
}
