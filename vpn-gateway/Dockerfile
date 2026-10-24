FROM alpine:3.20

# wireguard-tools: wg/wg-quick, iproute2: ip/sysctl, iptables: isolation rules
RUN apk add --no-cache wireguard-tools iproute2 iptables

COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

ENTRYPOINT ["/entrypoint.sh"]
