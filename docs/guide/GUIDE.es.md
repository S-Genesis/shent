# SHENT: un sistema de efectivo electrónico comunitario entre pares

**Idiomas:** [Español](GUIDE.es.md) · [English](GUIDE.en.md) · [Português](GUIDE.pt.md) · [Français](GUIDE.fr.md) · [Deutsch](GUIDE.de.md) · [Italiano](GUIDE.it.md) · [Русский](GUIDE.ru.md) · [中文](GUIDE.zh.md) · [日本語](GUIDE.ja.md) · [Türkçe](GUIDE.tr.md)

---

## Resumen

Una versión puramente entre pares de efectivo electrónico permitiría enviar pagos de una persona a otra
sin pasar por ninguna institución. Las firmas digitales resuelven una parte del problema, pero los
beneficios principales se pierden si todavía hace falta un tercero de confianza para impedir que una misma
moneda se gaste dos veces. SHENT resuelve el doble gasto con una red entre pares. La red registra las
transacciones en una cadena de bloques, y cada bloque está sellado por una prueba de trabajo. El registro
resultante no puede cambiarse sin rehacer ese trabajo. La cadena más larga, medida en trabajo acumulado y no
en cantidad de bloques, sirve como prueba de la secuencia de hechos y como prueba de que salió del mayor
poder de cómputo. Mientras la mayor parte de ese poder esté en manos de nodos que no cooperan para atacar la
red, esos nodos generan la cadena más larga y superan a cualquier atacante. La red en sí requiere una
estructura mínima. Los mensajes se difunden con el mejor esfuerzo posible, y los nodos pueden irse y volver
cuando quieran, aceptando la cadena con más trabajo como prueba de lo que pasó mientras no estaban.

SHENT es una moneda comunitaria. El bloque génesis lo dice en su propio texto:
`SHENT 13/10/2026 - moneda comunitaria sin valor monetario`. No cotiza en ningún mercado, la aplicación no
compra ni vende nada y nadie promete que vaya a valer algo.

---

## 1. Introducción

El dinero electrónico que usamos a diario depende casi siempre de intermediarios que procesan los pagos.
El sistema funciona, pero tiene las debilidades propias de un modelo basado en la confianza: las
transacciones se pueden revertir, los costos de mediación limitan los pagos pequeños y cada parte tiene que
entregar más información de la necesaria.

Lo que hace falta es un sistema de pago basado en prueba criptográfica en lugar de confianza, que permita a
dos personas cualesquiera operar directamente entre sí. Las transacciones que son computacionalmente
imposibles de revertir protegen a quien recibe. La red, que registra el orden de las transacciones y lo
sella con trabajo, protege contra el doble gasto.

SHENT es una implementación de esa idea: un nodo completo con billetera, minería y red P2P, que corre en
una computadora personal común.

## 2. Cuentas y transacciones

SHENT usa un modelo de cuentas. Cada cuenta se identifica con una **dirección**, que se obtiene de una clave
pública Ed25519: los primeros 20 bytes de su SHA-256, codificados en Bech32m con el prefijo de la red
(`shent1…` en la red principal, `tshent1…` en la de pruebas, `rshent1…` en la local). La dirección no
revela la clave pública hasta que la cuenta envía su primera transacción.

Cada cuenta tiene un **saldo** y un **nonce**, un contador de transacciones enviadas. Una transferencia
contiene:

- la clave pública de quien envía,
- la dirección de destino,
- el importe y la comisión,
- el nonce siguiente de la cuenta,
- una altura de expiración opcional,
- un memo opcional de hasta 80 bytes,
- y una firma Ed25519 sobre todo lo anterior y sobre el identificador de la red.

El nonce cumple la función que en otros diseños cumple la referencia a salidas anteriores: cada transacción
de una cuenta solo puede usarse una vez y en orden. Dos transacciones con el mismo nonce son un intento de
doble gasto, y la red acepta como máximo una. Como la firma incluye el identificador de la red, una
transacción válida en la red de pruebas no sirve en la principal.

Los importes se guardan como enteros: 1 SHENT son 100.000.000 unidades. No se usan números de punto
flotante para el dinero.

## 3. Bloques y cadena

Cada bloque contiene una cabecera y una lista de transacciones. La cabecera incluye el hash del bloque
anterior, la raíz Merkle de las transacciones, la marca de tiempo, el objetivo de dificultad y el nonce de
la prueba de trabajo. Como cada bloque incluye el hash del anterior, forman una cadena. Para cambiar un bloque
habría que rehacer el trabajo de ese bloque y de todos los que vinieron después.

Un bloque solo es válido si se cumplen estas condiciones:

- La marca de tiempo es mayor que la mediana de los 11 bloques anteriores y no está más de 5 minutos en el
  futuro.
- El bloque pesa como máximo 1.000.000 de bytes.
- La primera transacción es la recompensa del minero (coinbase) y paga exactamente lo que corresponde.
- Todas las demás transacciones tienen firmas válidas, nonces en orden y saldo suficiente.

## 4. Prueba de trabajo

Para sellar un bloque hay que encontrar un nonce tal que el hash de la cabecera quede por debajo del objetivo
de dificultad. SHENT usa **Argon2id** como función de hash para la prueba de trabajo, con 2 MiB de memoria por
intento. Es una función "memory-hard": cada intento necesita memoria además de cálculo, lo que acerca el
rendimiento de una CPU personal al de equipos especializados. No los vuelve imposibles: solo reduce la
ventaja.

Encontrar ese nonce no tiene atajo: cada intento es independiente y la probabilidad de éxito es la misma en
cada uno. Verificarlo, en cambio, cuesta un solo hash.

La dificultad se recalcula en cada bloque mirando los últimos 60, con el objetivo de que salga **un bloque
cada 10 minutos en promedio**, haya pocos o muchos mineros. Si se suma potencia, la dificultad sube; si se
retira, baja.

## 5. Red

Los pasos que sigue la red son:

1. Las transacciones nuevas se difunden a todos los nodos.
2. Cada nodo reúne transacciones nuevas en un bloque.
3. Cada nodo que mina busca la prueba de trabajo de su bloque.
4. Cuando un nodo la encuentra, difunde el bloque a todos los demás.
5. Los demás nodos aceptan el bloque solo si todas sus transacciones son válidas y no se gastaron antes.
6. Los nodos expresan su aceptación trabajando en el bloque siguiente, usando el hash del aceptado como
   bloque anterior.

Un nodo no confía en ningún otro. Cada bloque y cada transacción se verifican localmente con las reglas de
consenso, aunque lleguen de un nodo conocido. Un mensaje de la red no puede acreditar monedas por sí mismo:
solo un bloque válido dentro de la cadena con más trabajo cambia los saldos.

Para encontrarse, los nodos no dependen de un servidor central. Usan tres vías al mismo tiempo:

- la DHT pública de BitTorrent, donde cada nodo publica su puerto bajo una clave derivada de la red y de su
  bloque génesis;
- el descubrimiento en la red local;
- el intercambio de direcciones entre pares.

Cada dirección encontrada pasa por un handshake en el que ambos lados firman un desafío aleatorio con su
identidad Ed25519. Para que la red funcione basta con que al menos un nodo acepte conexiones entrantes (puerto
TCP 37373 abierto en la red principal). Los nodos sin puertos abiertos pueden recibir conexiones a través de
un relay.

## 6. Incentivo

Por convención, la primera transacción de cada bloque crea monedas nuevas que pertenecen a quien lo minó.
Es el incentivo para sostener la red y la única forma en que las monedas entran en circulación, sin una
autoridad que las emita.

- La recompensa es de **50 SHENT por bloque**.
- Se reduce a la mitad cada **1.000.000 de bloques** (unos 19 años con bloques de 10 minutos).
- La suma nunca supera **100.000.000 SHENT**.
- Además, quien mina cobra las comisiones de las transacciones que incluye.

La recompensa es para quien encuentra el bloque y no se reparte. Con muchos mineros, cada uno encuentra
bloques con una frecuencia proporcional a su parte del poder de cómputo total. En promedio el resultado es
el mismo que un reparto, pero en forma de premios enteros y aleatorios.

El incentivo también ayuda a que los nodos se mantengan honestos. Quien reuniera más poder de cómputo que
todos los demás juntos tendría que elegir entre usarlo para reescribir la historia o para ganar monedas
nuevas siguiendo las reglas. Le conviene más jugar dentro de las reglas que socavar el sistema del que depende
su propia riqueza.

## 7. Cadenas en competencia

Puede ocurrir que dos nodos encuentren un bloque casi al mismo tiempo, o que un grupo de nodos haya estado
desconectado y minado por su cuenta. Entonces existen dos versiones de la cadena. Los nodos siguen
trabajando sobre la primera que recibieron, pero guardan la otra por si llega a superarla. El empate se
rompe cuando aparece el bloque siguiente: la rama con **más trabajo acumulado** pasa a ser la válida en
todos los nodos.

Las cadenas no se combinan. Los bloques de la rama perdedora se descartan y, con ellos, sus recompensas.
Las transacciones que contenían vuelven a quedar pendientes si siguen siendo válidas. Un bloque minado
siguiendo las reglas conserva su recompensa mientras permanezca en la cadena con más trabajo. Por eso
conviene esperar algunas confirmaciones antes de dar por firme un pago grande, y conviene minar con la red
conectada: un nodo aislado puede minar bloques válidos que después pierdan contra la cadena del resto.

Hay dos reglas de protección:

- **Profundidad máxima de reorganización:** un nodo no reemplaza más de 288 bloques ya aceptados (unos dos
  días).
- **Puntos de control:** el programa publicado incluye los hashes de algunos bloques ya enterrados en la
  historia.

Las dos limitan el daño de un ataque largo, a cambio de un poco de confianza en el software que se descarga.
Son una decisión de diseño deliberada y conocida.

## 8. Privacidad

El modelo bancario tradicional logra privacidad restringiendo el acceso a la información. En SHENT, como en
cualquier registro público, todas las transacciones son visibles: direcciones, importes y momentos. La
privacidad se conserva de otra forma: las direcciones no tienen nombre.

Medidas que incluye:

- **Direcciones nuevas para cobrar.** La billetera genera una dirección nueva cada vez que se pide
  "Recibir". Todas salen del mismo respaldo, pero vistas desde afuera no tienen relación entre sí… hasta que
  se usan juntas en un pago.
- **Dandelion++.** Las transacciones propias pasan primero por un camino aleatorio de nodos y recién después
  se difunden, para que no sea evidente desde qué IP salieron.
- **Tor.** El nodo puede conectarse solo a través de un proxy SOCKS5. En ese modo no escucha conexiones ni
  usa la DHT, la red local ni UPnP.

Sobre el cifrado, para que no haya malentendidos:

- **Las claves privadas sí van cifradas**, solo en la computadora de cada persona.
- **La cadena de bloques no va cifrada**, a propósito. Cada nodo tiene que poder leer y comprobar cada
  transacción. Bloques, transferencias y saldos se comparten casi en tiempo real entre los nodos, en claro.
  No se pueden falsificar porque van firmados y sellados con trabajo, no porque estén ocultos.
- **El protocolo P2P tampoco va cifrado**, igual que el de Bitcoin. Quien observe la conexión puede ver que
  la computadora corre SHENT y qué datos públicos pasan. Para ocultar la IP está el modo Tor.

## 9. Billetera y respaldo

Las claves se generan en la propia computadora con el generador aleatorio del sistema operativo. Se guardan
en un archivo `.shent`, cifrado con una contraseña maestra (Argon2id + ChaCha20-Poly1305). La clave privada
nunca sale de esa computadora ni se envía por la red.

- Sin el archivo y sin la contraseña, las monedas no se pueden recuperar. No existe un mecanismo de
  recuperación: nadie puede restablecer la contraseña.
- Hay que guardar una copia del archivo `.shent` fuera de la computadora (un pendrive, una nube) y la
  contraseña en un lugar seguro.
- El archivo, la contraseña y la clave no deben compartirse con nadie. Ningún desarrollador ni nodo va a
  pedirlos nunca.

## 10. Seguridad y límites

Un atacante con más poder de cómputo que el resto de la red podría:

- reescribir los bloques recientes,
- revertir sus propias transacciones (doble gasto),
- impedir que se confirmen transacciones ajenas.

En cambio, no podría:

- crear monedas de la nada,
- gastar monedas de una cuenta cuya clave no tiene,
- cambiar las reglas.

Los nodos honestos rechazan cualquier bloque inválido, venga de donde venga.

La seguridad depende de cuánta potencia honesta haya. En una red joven, con pocos mineros, una sola
computadora potente puede tener la mayoría. La página de Estadísticas de la aplicación muestra cuántas
direcciones minaron en los últimos días y avisa cuando una sola concentra más de la mitad de los bloques.

Este software se entrega tal cual, sin garantías. Ningún sistema es invulnerable. Las reglas, el código y
las pruebas están a la vista para que cualquiera los revise.

## 11. Uso práctico

### Instalar (Windows 10/11, 64 bits)

1. Descargar `SHENT-<versión>-win-x64.zip` desde la página de *Releases* del repositorio.
2. Verificar el hash con `Get-FileHash .\SHENT-<versión>-win-x64.zip -Algorithm SHA256` y compararlo
   con el archivo `.sha256` publicado.
3. Descomprimir el zip completo y abrir `SHENT.exe`. No hace falta instalar nada más.
4. Si el firewall de Windows lo pregunta, permitir el acceso para que otros nodos puedan conectarse.

### Primeros pasos

- **Billetera → Crear billetera.** Elegir una contraseña maestra y guardar el respaldo `.shent`.
- **Minería.** Elegir hilos e intensidad y encender el reactor. Si no hay pares conectados, la aplicación lo
  avisa: lo minado sin red puede descartarse.
- **Enviar.** Pegar la dirección de destino, el importe y la comisión. Revisar la pantalla de confirmación
  antes de firmar.
- **Recibir.** Compartir la dirección que se muestra. Es pública y no permite gastar nada.
- **Idioma.** El selector con el globo cambia la interfaz entre 10 idiomas. Los importes se escriben con el
  separador decimal del idioma elegido. Si un importe es ambiguo, la aplicación lo rechaza en lugar de
  adivinar.

### Hacer más fuerte la red

Abrir en el router el puerto **TCP 37373** hacia la computadora, o activar UPnP. Cada nodo que acepta
conexiones facilita que otros se sumen. Para un servidor sin pantalla está el nodo de consola:
`nodo-consola\shent-node.exe help`.

### Compilar desde el código

Requiere el SDK de .NET 10.

```
dotnet build Shent.sln
dotnet test tests/Shent.Core.Tests
dotnet run --project src/Shent.Desktop
```

El release para Windows se genera con `scripts/publish-windows.ps1`. La documentación técnica de la red está
en [`docs/P2P_INTERNET.md`](../P2P_INTERNET.md).

## 12. Conclusión

Propusimos un sistema de transacciones electrónicas que no depende de la confianza. Las firmas digitales
dan el control de las monedas a quien tiene la clave. Para impedir el doble gasto, la red registra la
historia pública de transacciones en una cadena de prueba de trabajo, que se vuelve imposible de alterar
para un atacante mientras los nodos honestos tengan la mayor parte del poder de cómputo. La red es robusta
por su simplicidad: los nodos trabajan a la vez con muy poca coordinación, no necesitan identificarse y
pueden irse y volver cuando quieran, aceptando la cadena con más trabajo como prueba de lo ocurrido. Votan
con su poder de cómputo: aceptan los bloques válidos trabajando sobre ellos y rechazan los inválidos
negándose a hacerlo. Todas las reglas e incentivos necesarios se imponen con ese único mecanismo de
consenso.

SHENT existe para que una comunidad lo use, lo estudie y lo mejore.
