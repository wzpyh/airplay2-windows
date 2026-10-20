/*
 * mdns-pub: publish an AirPlay 2 (_airplay._tcp) + AirPlay 1 (_raop._tcp) service pair
 * on Windows using UxPlay's minimal mDNS responder (lib/mdnsd, LGPL-2.1).
 *
 * Driven over stdin by shairport-sync's "pipe" mDNS backend, one line per (re)registration:
 *   S \t <ap2 name> \t <ap1 name> \t <port> \t <ap1 txt count> \t <ap1 k=v>... \t <ap2 k=v>...
 * EOF on stdin (shairport-sync exited) sends goodbye packets and exits.
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "mdnsd.h"

#ifdef _WIN32
#include <winsock2.h>
#endif

static void fill_txt(mdnsd_txt_t *txt, char **kv, int n) {
    memset(txt, 0, sizeof(*txt));
    for (int i = 0; i < n; i++) {
        char *eq = strchr(kv[i], '=');
        if (!eq) {
            mdnsd_txt_add(txt, kv[i], "");
            continue;
        }
        *eq = '\0';
        if (mdnsd_txt_add(txt, kv[i], eq + 1) != 0)
            fprintf(stderr, "mdns-pub: TXT record full, dropped %s\n", kv[i]);
        *eq = '=';
    }
}

int main(int argc, char **argv) {
#ifdef _WIN32
    WSADATA wsa;
    WSAStartup(MAKEWORD(2, 2), &wsa);
#endif
    /* host name for the SRV/A records: argv[1], else "<computer name>.local" */
    char host[MDNSD_MAX_NAME];
    if (argc > 1) {
        snprintf(host, sizeof(host), "%s", argv[1]);
    } else {
        char name[MDNSD_MAX_NAME - 8] = "airplay2-windows";
        gethostname(name, sizeof(name));
        snprintf(host, sizeof(host), "%s.local", name);
    }
    setvbuf(stderr, NULL, _IONBF, 0);

    mdnsd_t *mdnsd = mdnsd_init(host);
    if (!mdnsd || mdnsd_start(mdnsd) != 0) {
        fprintf(stderr, "mdns-pub: failed to start mDNS responder\n");
        return 1;
    }

    static mdnsd_service_t airplay, raop;
    int have = 0;
    static char line[16384];
    while (fgets(line, sizeof(line), stdin)) {
        line[strcspn(line, "\r\n")] = '\0';
        char *f[128];
        int n = 0;
        for (char *p = strtok(line, "\t"); p && n < 128; p = strtok(NULL, "\t"))
            f[n++] = p;
        if (n < 5 || strcmp(f[0], "S") != 0) {
            fprintf(stderr, "mdns-pub: ignoring malformed line\n");
            continue;
        }
        int port = atoi(f[3]);
        int n1 = atoi(f[4]);
        if (n1 < 0 || 5 + n1 > n) {
            fprintf(stderr, "mdns-pub: bad ap1 txt count\n");
            continue;
        }
        memset(&airplay, 0, sizeof(airplay));
        memset(&raop, 0, sizeof(raop));
        snprintf(airplay.type, sizeof(airplay.type), "_airplay._tcp.local");
        snprintf(airplay.name, sizeof(airplay.name), "%s._airplay._tcp.local", f[1]);
        airplay.port = (unsigned short)port;
        airplay.registered = 1;
        fill_txt(&airplay.txt, f + 5 + n1, n - 5 - n1);
        snprintf(raop.type, sizeof(raop.type), "_raop._tcp.local");
        snprintf(raop.name, sizeof(raop.name), "%s._raop._tcp.local", f[2]);
        raop.port = (unsigned short)port;
        raop.registered = 1;
        fill_txt(&raop.txt, f + 5, n1);
        mdnsd_set_services(mdnsd, &airplay, &raop);
        mdnsd_announce(mdnsd, MDNSD_TTL_SERVICE);
        have = 1;
        fprintf(stderr, "mdns-pub: announced \"%s\" / \"%s\" port %d (txt %d + %d)\n", f[1], f[2],
                port, n - 5 - n1, n1);
    }

    if (have) {
        mdnsd_goodbye(mdnsd, &airplay);
        mdnsd_goodbye(mdnsd, &raop);
    }
    mdnsd_stop(mdnsd);
    mdnsd_destroy(mdnsd);
    return 0;
}
