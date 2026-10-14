# SHENT por Internet: conectar nodos entre PCs distintas

Esta guía explica cómo armar una red SHENT real entre tu PC y las de tus amigos, cada una en su casa,
con su propia conexión a Internet. Todo lo que aparece en MAYÚSCULAS (`IP_PUBLICA`, `PEER_ID`,
`IP_DEL_SERVIDOR`, `DOMINIO`…) es un marcador: **reemplazalo por los datos reales**. Las IPs de la
forma `203.0.113.x` o `198.51.100.x` que aparecen en algunos ejemplos son direcciones de documentación
(RFC 5737) y no corresponden a ningún nodo.

> SHENT es una moneda comunitaria.

---

## 1. Cómo funciona la red

| Pieza | Qué hace | Qué NO hace |
|---|---|---|
| **Nodo** (`shent-node` o la app de escritorio) | Guarda la cadena, valida cada bloque y transacción con las reglas de consenso, mina, reenvía bloques y transacciones. | No confía en ningún par: un bloque inválido se rechaza aunque venga de un nodo conocido. |
| **Peer ID** (`peer1…`) | Identidad criptográfica persistente del nodo (Ed25519, en `node_key.json`). En cada conexión ambos lados firman un desafío aleatorio. | No es una billetera ni contiene fondos. |
| **Nodo de arranque (bootstrap)** | Cualquier nodo cuya dirección conocés. Sirve para encontrar los primeros pares. | No controla saldos, no decide qué bloques son válidos, no es una autoridad. Si se apaga, la red sigue. |
| **Relay** | Un nodo con IP pública que reenvía bytes para que nodos sin puertos abiertos (NAT/CGNAT) puedan recibir conexiones. | Puede ver el tráfico (el protocolo P2P no va cifrado), pero no puede falsificar bloques ni transacciones: los extremos se autentican entre sí y cada nodo valida todo. Tampoco valida nada por nadie. |
| **API local** | `http://127.0.0.1:37380/api/p2p/…`: estado, pares e identidad, en solo lectura. | No acepta escrituras, no expone claves y no escucha fuera de tu PC. |

Puertos por red:

| Red | P2P (TCP) | Descubrimiento LAN (UDP) | API local (TCP, solo 127.0.0.1) |
|---|---|---|---|
| mainnet | 37373 | 37375 | 37380 |
| testnet | 47373 | 47375 | 47380 |
| regtest | 57373 | 57375 | 57380 |

### Descubrimiento automático (no hace falta pasar direcciones a mano)

Cada nodo encuentra a los demás solo, por tres vías que funcionan a la vez:

1. **DHT pública de BitTorrent (Internet).** Es una red de millones de nodos que nadie controla. Cada nodo
   SHENT publica su puerto TCP bajo la clave `SHA-1("SHENT-DHT-V1:" + red + ":" + hash del génesis)` y busca
   esa misma clave para obtener IP:puerto de otros nodos SHENT. Se consulta al arrancar, cada 20 s–5 min
   mientras no hay pares y cada 15 min cuando ya está conectado. Desactivable con `dht=0`, `SHENT_DHT=0`
   o `--no-dht`.
2. **Red local (UDP 37375).** Los nodos de la misma casa u oficina se encuentran en un segundo.
3. **Intercambio entre pares.** Cada nodo comparte las direcciones verificadas que conoce, y las guarda en
   `peers.json`: en el siguiente arranque reconecta al instante sin esperar a la DHT.

La DHT solo **sugiere** direcciones: cualquiera puede publicar basura bajo la misma clave. Por eso cada
dirección tiene que pasar el handshake firmado (misma red, mismo génesis, Peer ID con firma Ed25519), las
que fallan se descartan con espera creciente, no se reenvían a otros pares hasta haberse verificado, y cada
bloque y transacción se valida con las reglas de consenso en tu PC. Un nodo falso no puede darte bloques
inválidos, monedas inventadas ni saldos alterados.

**Límite importante:** para conectarse hace falta que **al menos un nodo de la red acepte conexiones
entrantes** (puerto TCP abierto en el router, UPnP activo o un VPS). Dos PCs sin puertos abiertos se
encuentran en la DHT pero no pueden conectarse entre sí (no hay "hole punching"). Cuantos más nodos con
el puerto abierto, más robusta es la red.

Consenso: un bloque cada ~10 minutos, 50 SHENT por bloque, halving cada 1.000.000 de bloques (máximo
100.000.000 SHENT). Si dos nodos minan a la vez, gana la cadena **válida con más trabajo acumulado**; la
otra rama se descarta en todos los nodos (reorganización).

### Formato de direcciones

```
/ip4/IP_PUBLICA/tcp/37373/p2p/PEER_ID                                    directa por IP
/dns4/DOMINIO/tcp/37373/p2p/PEER_ID                                      directa por nombre DNS
/ip4/IP_RELAY/tcp/37373/p2p/PEER_ID_RELAY/p2p-circuit/p2p/PEER_ID        a través de un relay
```

La parte `/p2p/PEER_ID` es opcional, pero recomendada: con ella el nodo verifica que del otro lado esté
**ese** nodo y no otro. También se acepta el formato clásico `IP:puerto`.

---

## 2. Instalar

**Para usuarios (Windows 10/11 de 64 bits):** descomprimir `SHENT-0.1.0-win-x64.zip` y abrir `SHENT.exe`.
No requiere instalar .NET ni el runtime de Visual C++ (viaja `vcruntime140.dll` junto al .exe).
Para generar ese zip: `.\scripts\publish-windows.ps1` (queda en `release\`). Windows 7 y 8.1 no son
compatibles con .NET 10.

Necesitás el SDK de .NET 10 solo para compilar; el ejecutable publicado no requiere .NET instalado.

**Windows (PowerShell):**

```powershell
cd C:\ruta\a\shent
dotnet publish src/Shent.Node -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/shent-node
.\publish\shent-node\shent-node.exe help
```

**Linux (x64):**

```bash
cd ~/shent
dotnet publish src/Shent.Node -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/shent-node
./publish/shent-node/shent-node help
```

(En macOS: `-r osx-arm64` o `-r osx-x64`.) Los scripts `scripts/start-node.ps1` y
`scripts/start-node.sh` usan el ejecutable publicado si existe y, si no, compilan con `dotnet run`.

> **Si ya habías corrido SHENT antes de este cambio:** el protocolo pasó a la versión 2 y el génesis de
> mainnet/testnet cambió con los bloques de 10 minutos. Todos los nodos tienen que actualizarse, y hay
> que borrar el `chain.db` viejo de esa red (`%APPDATA%\SHENT\mainnet\chain.db` en Windows,
> `~/.shent/mainnet/chain.db` en Linux). Las billeteras (`*.shent`) no se tocan.

---

## 3. Nodo 1 (el primero de la red)

### 3.1 Ver el Peer ID

```powershell
# Windows
.\publish\shent-node\shent-node.exe id
```

```bash
# Linux
./publish/shent-node/shent-node id
```

Salida (tu Peer ID será otro):

```
Red:      mainnet
Peer ID:  peer1…
Puerto:   TCP 37373

Dirección para compartir (reemplazá IP_PUBLICA por la IP pública de esta conexión o por un dominio):
  /ip4/IP_PUBLICA/tcp/37373/p2p/peer1…
```

El Peer ID se guarda en `<carpeta de datos>/mainnet/node_key.json` y no cambia entre reinicios. No lo
borres: si lo borrás, el nodo genera otro ID y tus amigos tendrán que actualizar la dirección.

### 3.2 Abrir el puerto

1. **Firewall de la PC** (una sola vez):
   - Windows (PowerShell como administrador):
     ```powershell
     New-NetFirewallRule -DisplayName "SHENT P2P" -Direction Inbound -Protocol TCP -LocalPort 37373 -Action Allow
     ```
   - Linux con ufw:
     ```bash
     sudo ufw allow 37373/tcp
     ```
2. **Router:** el nodo intenta abrir el puerto solo con **UPnP**. Si tu router no tiene UPnP (o está
   desactivado), entrá a su configuración y creá un *port forwarding* TCP 37373 → IP local de tu PC
   (la IP local aparece en el log como `Dirección local: /ip4/192.168.x.x/...`). Usá el **mismo número**
   de puerto afuera y adentro.
3. **Averiguá tu IP pública** desde la página de estado del router, o con un servicio externo:
   - Windows: `Invoke-RestMethod https://api.ipify.org`
   - Linux: `curl https://api.ipify.org`

### 3.3 Iniciar

```powershell
# Windows
.\publish\shent-node\shent-node.exe
# o con minería (tu dirección shent1… sale de la app de escritorio, pestaña Billetera)
.\publish\shent-node\shent-node.exe --mine --address shent1TU_DIRECCION --threads 2
```

```bash
# Linux
./publish/shent-node/shent-node
```

Lo que tenés que ver en el log:

```
[p2p] Peer ID de este nodo: peer1…
[p2p] Escuchando conexiones P2P en el puerto TCP 37373.
[upnp] Puerto TCP 37373 abierto en el router por UPnP. IP externa del router: …   (si hay UPnP)
[api] API local de diagnóstico en http://127.0.0.1:37380/api/p2p/status
```

Pasale a tu amigo: `/ip4/TU_IP_PUBLICA/tcp/37373/p2p/TU_PEER_ID`.

Cuando otro nodo se conecte, el tuyo le pide que intente conectarse de vuelta (prueba de
alcanzabilidad). Si funciona, vas a ver:

```
[p2p] Este nodo es accesible desde Internet. Compartí esta dirección: /ip4/…/tcp/37373/p2p/peer1…
```

Esa es la dirección verificada: también aparece en la pestaña **Red P2P** de la app y en
`/api/p2p/info`.

---

## 4. Nodo 2 (un amigo que se une)

```powershell
# Windows: reemplazá IP_PUBLICA y PEER_ID por los del nodo 1
$env:BOOTSTRAP_NODES = "/ip4/IP_PUBLICA/tcp/37373/p2p/PEER_ID"
.\publish\shent-node\shent-node.exe
# equivalente sin variable:
.\publish\shent-node\shent-node.exe --bootstrap /ip4/IP_PUBLICA/tcp/37373/p2p/PEER_ID
# o con el script:
.\scripts\start-node.ps1 -Bootstrap "/ip4/IP_PUBLICA/tcp/37373/p2p/PEER_ID"
```

```bash
# Linux
BOOTSTRAP_NODES=/ip4/IP_PUBLICA/tcp/37373/p2p/PEER_ID ./publish/shent-node/shent-node
```

En la app de escritorio: **Red P2P → Conectar con otro nodo**, pegá la dirección; o **Ajustes → Nodos
de arranque** (queda guardado en `shent.conf`).

Varios nodos de arranque: separalos con coma en `BOOTSTRAP_NODES`, o repetí `--bootstrap`.

Si la dirección tiene un Peer ID que no coincide con el del nodo que responde, la conexión se rechaza:

```
[p2p] 203.0.113.10:37373 respondió con el Peer ID peer1…, pero se esperaba peer1…. Revisá la dirección configurada; la conexión se rechaza.
```

---

## 5. Comprobar que funciona

En el log del nodo 2:

```
[p2p] Conectado a peer1abcd…wxyz en IP_PUBLICA:37373 (saliente, SHENT/0.1.0, altura anunciada 120).
[sync] Sincronizando con IP_PUBLICA:37373 (altura anunciada 120, sin verificar).
[sync] Cadena sincronizada con IP_PUBLICA:37373 en la altura 120.
```

Con la API local (en cada PC, solo responde a esa misma PC):

```powershell
# Windows
Invoke-RestMethod http://127.0.0.1:37380/api/p2p/status
Invoke-RestMethod http://127.0.0.1:37380/api/p2p/peers
Invoke-RestMethod http://127.0.0.1:37380/api/p2p/info
```

```bash
# Linux
curl -s http://127.0.0.1:37380/api/p2p/status
curl -s http://127.0.0.1:37380/api/p2p/peers
curl -s http://127.0.0.1:37380/api/p2p/info
```

Campos clave de `/api/p2p/status`: `peerId`, `height`, `tipHash`, `bestKnownHeight` (altura anunciada
por pares, **no verificada**), `syncing`, `syncProgress`, `peers.connected`, `reachability`
(`public`, `private` o `unknown`), `relay`.

Transacciones y bloques: enviá SHENT desde la app (pestaña Billetera) en cualquiera de las dos PCs. La
transacción aparece en el mempool de la otra en segundos, y cuando cualquiera de los dos mina un bloque,
el otro lo recibe, lo valida y lo agrega. Un mensaje P2P nunca acredita monedas por sí solo: los saldos
cambian únicamente cuando un bloque válido entra en la cadena.

---

## 6. NAT, CGNAT y relay

| Situación | ¿Conexiones entrantes directas? | Qué hacer |
|---|---|---|
| Router con UPnP | Sí, automático | Nada |
| Router sin UPnP, IP pública | Sí, si abrís el puerto | Port forwarding TCP 37373 + firewall |
| **CGNAT** (el proveedor comparte tu IP con otros clientes) | **No**, aunque abras puertos en tu router | Relay, o pedirle una IP pública al proveedor, o IPv6 |
| Red de empresa / universidad / datos móviles | Normalmente no | Relay |

Un nodo **sin puertos abiertos igual puede participar por completo**: se conecta hacia afuera, sincroniza,
mina y propaga. Lo único que no puede es recibir conexiones; si **todos** los nodos están así, nadie
puede conectarse a nadie y hace falta al menos un nodo accesible (con puerto abierto) o un relay.

**Cómo saber si tenés CGNAT:** mirá la IP "WAN" o "Internet" en la página del router. Si está entre
`100.64.0.0` y `100.127.255.255`, o es privada (`10.x`, `172.16–31.x`, `192.168.x`), o no coincide con
la que muestra `api.ipify.org`, estás detrás de CGNAT o doble NAT. El nodo lo avisa cuando UPnP le
informa una IP privada:

```
[upnp] El router recibe una IP privada (100.72.x.x): tu proveedor usa CGNAT o hay doble NAT. Abrir puertos no alcanza; …
```

**Qué hace el nodo solo:**
1. Intenta UPnP.
2. Pide a sus pares que prueben conectarse de vuelta (máximo 3 por ronda, repetido cada 10–30 min).
3. Si nadie llega (`reachability: private`) y hay algún par con relay activado, reserva un lugar en hasta
   dos relays y anuncia su dirección de circuito:
   ```
   [relay] Reserva obtenida en el relay IP_RELAY:37373. Otros nodos pueden conectarse a esta PC con: /ip4/IP_RELAY/tcp/37373/p2p/PEER_ID_RELAY/p2p-circuit/p2p/TU_PEER_ID
   ```
   Esa dirección se difunde sola a los demás nodos; también la podés compartir a mano.

**No implementado: hole punching.** No hay perforación de NAT (DCUtR/STUN): con transporte TCP es poco
confiable, y QUIC necesita bibliotecas nativas que no vienen en todos los sistemas. Cuando la conexión
directa no es posible, se usa el relay. No hay garantía de conexión directa sin puertos abiertos.

---

## 7. Desplegar un nodo de arranque + relay (VPS con IP pública)

Recomendado para que la red no dependa de la PC de nadie. Cualquier VPS Linux barato sirve (1 vCPU,
1 GB de RAM). El relay limita reservas (128), circuitos (64, máx. 8 por destino), duración (2 h) y
volumen (512 MiB por circuito).

```bash
# En el VPS (Ubuntu/Debian). Compilá en tu PC con -r linux-x64 y copiá el ejecutable:
#   scp publish/shent-node/shent-node usuario@IP_DEL_SERVIDOR:/tmp/
sudo useradd --system --home /var/lib/shent --shell /usr/sbin/nologin shent
sudo mkdir -p /opt/shent /var/lib/shent
sudo install -m 755 /tmp/shent-node /opt/shent/shent-node
sudo chown -R shent:shent /var/lib/shent
sudo ufw allow 37373/tcp

# Peer ID del servidor (anotalo):
sudo -u shent /opt/shent/shent-node id --datadir /var/lib/shent
```

`/etc/systemd/system/shent.service` (reemplazá `IP_DEL_SERVIDOR`):

```ini
[Unit]
Description=SHENT node (arranque + relay)
After=network-online.target
Wants=network-online.target

[Service]
User=shent
Group=shent
ExecStart=/opt/shent/shent-node --network mainnet --datadir /var/lib/shent --relay --no-upnp --announce /ip4/IP_DEL_SERVIDOR/tcp/37373
Restart=always
RestartSec=5
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=/var/lib/shent
PrivateTmp=true

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now shent
journalctl -u shent -f                       # logs
curl -s http://127.0.0.1:37380/api/p2p/status # estado (desde el propio servidor)
```

Todos los nodos usan entonces:

```
BOOTSTRAP_NODES=/ip4/IP_DEL_SERVIDOR/tcp/37373/p2p/PEER_ID_DEL_SERVIDOR
```

Podés poner dos o más servidores separados por coma. Si un servidor se cae, los nodos ya se conocen
entre sí (lista de pares en `peers.json`) y siguen conectados; el servidor no tiene ningún privilegio.

Para usar la API desde tu PC sin exponerla: `ssh -L 37380:127.0.0.1:37380 usuario@IP_DEL_SERVIDOR` y
abrí `http://127.0.0.1:37380/api/p2p/status`.

---

## 8. Prueba manual con dos PCs en conexiones distintas

Preparación: PC **A** (tu casa) y PC **B** (casa de un amigo, otro proveedor o datos móviles). Ambas con
la misma versión de SHENT. Usá **testnet** para no mezclar con mainnet: agregá `--network testnet` a
todos los comandos y usá el puerto 47373 (API 47380).

| # | Dónde | Acción | Resultado esperado |
|---|---|---|---|
| 1 | A | Abrí TCP 47373 en firewall y router (o confiá en UPnP). | — |
| 2 | A | `shent-node --network testnet id` | Muestra `Peer ID: peer1…`. Anotalo. |
| 3 | A | Averiguá la IP pública (`api.ipify.org`). | Anotala. |
| 4 | A | `shent-node --network testnet` | `Escuchando conexiones P2P en el puerto TCP 47373`. |
| 5 | B | `shent-node --network testnet --bootstrap /ip4/IP_PUBLICA_DE_A/tcp/47373/p2p/PEER_ID_DE_A` | `Conectado a peer1… en IP_PUBLICA_DE_A:47373 (saliente…)`. |
| 6 | A | Mirá el log. | `Conectado a peer1… en IP_DE_B:… (entrante…)`. |
| 7 | A y B | `curl -s http://127.0.0.1:47380/api/p2p/peers` | Cada uno lista al otro con su `peerId`. |
| 8 | B | Esperá ~1 min. | En B: `Este nodo es accesible…` si B tiene puerto abierto; si no, `reachability: private` (normal). |
| 9 | A | Mina: `--mine --address tshent1…` (reiniciá con la opción). | B recibe cada bloque: misma `height` y `tipHash` en `/api/p2p/status` de ambos. |
| 10 | B | Enviá SHENT de testnet a una dirección de A desde la app. | La transacción aparece en el mempool de A y, tras el siguiente bloque, en el saldo. |
| 11 | A | Cerrá el nodo (Ctrl+C) y volvé a abrirlo a los 30 s. | B registra la desconexión y se reconecta solo en segundos (espera progresiva 2 s → 5 min). |
| 12 | B | Cerrá B, borrá `chain.db` de testnet y volvé a abrirlo. | B sincroniza desde cero y llega a la misma altura (descarga en lotes y valida cada bloque). |
| 13 | A y B | Minen los dos a la vez un rato. | Puede haber reorganizaciones cortas (`Reorganización: …`), pero al final ambos tienen el mismo `tipHash`. |

Si en el paso 5 no conecta: probá desde B con `Test-NetConnection IP_PUBLICA_DE_A -Port 47373`
(Windows) o `nc -vz IP_PUBLICA_DE_A 47373` (Linux). Si falla, el puerto de A no está accesible (router,
firewall o CGNAT): usá un relay (sección 7) o invertí los papeles si B tiene IP pública.

---

## 9. Seguridad

- **Validación:** todo bloque se verifica completo (PoW Argon2id, estructura, firmas, saldos, nonces,
  emisión, dificultad) antes de aceptarlo, venga de quien venga. La altura y el trabajo que anuncia un
  par solo sirven para elegir de quién descargar; si resultan falsos, se corrigen y no se vuelve a
  pedir en bucle.
- **Límites:** tramas de máximo 8 KiB antes del handshake; 4 conexiones entrantes por IP; 64
  conexiones pendientes; límite de mensajes por segundo; listas de inventario, direcciones y localizadores
  acotadas; cola de envío acotada.
- **Penalizaciones:** un par que manda un bloque inválido, un mensaje malformado o una firma de identidad
  incorrecta queda baneado 24 h (por IP y por Peer ID). Si llegó por un relay, se banea el Peer ID y no
  la IP del relay.
- **Duplicados:** una sola conexión por Peer ID (si hay dos, ambos lados conservan la misma); los
  anuncios repetidos de bloques y transacciones se ignoran; una transacción rechazada no se vuelve a
  pedir hasta el próximo bloque.
- **`node_key.json`:** identifica al nodo; no tiene fondos. Si alguien lo copia podría hacerse pasar
  por tu nodo (no gastar tus monedas). En Linux/macOS se crea con permisos `600`. No lo compartas ni lo
  subas a git (está en `.gitignore`).
- **API local:** solo `127.0.0.1`, solo `GET`, verifica el encabezado `Host` (protección contra DNS
  rebinding), sin CORS, sin datos de billeteras ni claves.
- **Tráfico:** el protocolo P2P no está cifrado (como el de Bitcoin): los datos de la cadena son públicos
  de todos modos. La autenticación del handshake evita suplantar un Peer ID, pero alguien en el camino
  (por ejemplo tu proveedor) puede ver qué bloques y transacciones se intercambian. Con el modo proxy
  (Tor) el tráfico viaja cifrado hasta la salida de Tor.
- **Ataque del 51 %:** quien tenga más poder de minería que el resto junto puede reorganizar bloques
  recientes y demorar transacciones, pero no puede gastar monedas ajenas, crear monedas fuera de la
  emisión ni hacer aceptar un bloque inválido: cada nodo lo rechaza aunque lo manden muchos pares. Las
  reorganizaciones tienen un límite de 288 bloques y no pueden pasar por encima de un punto de control.
  Ninguna criptomoneda de prueba de trabajo es inmune a esto; por eso conviene esperar varias
  confirmaciones antes de dar un pago por definitivo.

### Privacidad de red

Lo que **sí** protege:

- **Dandelion++ (activado por defecto):** una transacción tuya no se anuncia a todos de entrada. Primero
  pasa de un nodo a otro por un camino aleatorio (fase "tallo") y recién después un nodo cualquiera la
  difunde. Así, quien observa la red no puede deducir fácilmente que salió de tu IP. Cada nodo del
  camino valida la transacción completa; si no la difunde nadie, el nodo que la envió lo hace solo al
  vencer un plazo (unos 45 a 135 segundos), así que nunca se pierde. Los nodos de versiones anteriores
  la reciben ya difundida. Desactivar: `dandelion=0`, `SHENT_DANDELION=0` o `--no-dandelion`.
- **Modo proxy / Tor:** con `proxy=127.0.0.1:9050` (Tor) o `127.0.0.1:9150` (Tor Browser abierto), todas
  las conexiones salen por Tor y los nombres de dominio también se resuelven por Tor. El nodo no escucha
  conexiones entrantes, no usa la DHT, ni la red local, ni UPnP, ni ofrece pruebas de alcanzabilidad:
  ningún par ve tu IP real. Si el proxy no responde, el nodo **no** se conecta directo (falla cerrado).
  Como la DHT no funciona por Tor, hacen falta pares conocidos: conviene abrir SHENT una vez sin proxy
  para que guarde algunos, o configurar nodos de arranque. Cada conexión usa un circuito de Tor distinto.
- **Una dirección nueva por cobro:** la billetera deriva direcciones de cobro desde la misma semilla
  (HKDF-SHA256, distintas por red). "Recibir" muestra una dirección sin usar y "Generar otra" crea una
  nueva, para que distintas personas que te pagan no vean que es la misma billetera. El respaldo de
  siempre sirve para todas: al importarlo en otra PC se recuperan las que tienen movimientos (se buscan
  hasta 20 seguidas sin uso). Si un envío necesita fondos de varias direcciones, la app avisa que eso
  las vincula públicamente.

Lo que **no** protege (no hay que prometerlo): en la cadena, como en Bitcoin, las direcciones de origen
y destino y los montos son públicos. Cualquiera puede ver qué dirección pagó a cuál y cuánto saldo
tiene cada dirección. Ocultar eso requiere cambiar el formato de las transacciones y el consenso
(firmas en anillo, montos ocultos), lo que obligaría a reiniciar la cadena; no está implementado.

### Puntos de control

`src/Shent.Core/Consensus/checkpoints.json` fija hashes de bloques ya muy enterrados. Un bloque a esa
altura con otro hash se rechaza, y si tu cadena ya pasó el último punto de control, una bifurcación que
empiece antes también. Se generan antes de cada release desde un nodo sincronizado:

```powershell
.\scripts\update-checkpoints.ps1              # mainnet, API en 127.0.0.1:37380
.\scripts\publish-windows.ps1                 # lo ejecuta solo (omitir con -SkipCheckpoints)
```

Solo se agregan alturas múltiplos de 1000 con al menos 288 confirmaciones; el script se niega si el
nodo está sincronizando, si es de otra red o si un punto nuevo contradice uno existente. Revisá el
cambio en git antes de publicar: un punto de control equivocado dejaría a los nodos nuevos fuera de la
cadena real.

---

## 10. Problemas frecuentes

| Síntoma | Causa probable | Solución |
|---|---|---|
| `No se pudo escuchar en el puerto 37373` | Otro programa (u otro nodo) usa el puerto | Cerrá el otro, o `--port 37374` (y abrí ese puerto) |
| Nunca aparece `Conectado a…` | Dirección o puerto incorrectos, puerto cerrado | Revisá IP/puerto, `Test-NetConnection` / `nc -vz` |
| `respondió con el Peer ID …, pero se esperaba …` | El Peer ID de la dirección está mal o el otro nodo regeneró su identidad | Pedí la dirección de nuevo (`shent-node id`) |
| `El par pertenece a otra red SHENT` | Uno usa mainnet y el otro testnet, o versiones con génesis distinto | Misma red y misma versión en todos |
| `Versión de protocolo demasiado antigua` | El otro nodo no está actualizado | Actualizar SHENT |
| `La base de datos pertenece a otra red o a otra versión del génesis` | `chain.db` de una versión anterior | Borrá el `chain.db` de esa red |
| `reachability: private` | NAT/CGNAT/firewall | Abrir puerto, o relay (se busca solo si algún par lo ofrece) |
| El reloj difiere mucho | Hora del sistema mal configurada | Activar la hora automática del sistema |

---

## 11. Decisiones técnicas

- **¿Por qué no libp2p?** No existe una implementación de libp2p mantenida y estable para .NET. Usar la
  de Go o Rust obligaría a un proceso aparte y a reescribir la red existente. Se reutilizó el protocolo
  propio de SHENT y se le agregó lo que aporta libp2p para este caso: Peer ID criptográfico,
  direcciones multiaddr, handshake autenticado, relay de circuitos (similar a Circuit Relay v2) y
  prueba de alcanzabilidad (similar a AutoNAT). Las direcciones usan la sintaxis multiaddr, pero el
  protocolo no es compatible con nodos libp2p.
- **Transporte:** TCP. QUIC no se incluyó porque en .NET depende de `libmsquic`, que no viene instalada
  en macOS ni en muchas distribuciones Linux.
- **Recuperación ante cortes:** la libreta de pares (`peers.json`) se guarda cada minuto. Al reiniciar,
  el nodo se reconecta a los pares conocidos aunque los nodos de arranque no respondan. La cadena
  (`chain.db`, SQLite) es transaccional: un corte a mitad de un bloque no deja la base a medias.
