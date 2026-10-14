# SHENT: um sistema de dinheiro eletrônico comunitário ponto a ponto

**Idiomas:** [Español](GUIDE.es.md) · [English](GUIDE.en.md) · [Português](GUIDE.pt.md) · [Français](GUIDE.fr.md) · [Deutsch](GUIDE.de.md) · [Italiano](GUIDE.it.md) · [Русский](GUIDE.ru.md) · [中文](GUIDE.zh.md) · [日本語](GUIDE.ja.md) · [Türkçe](GUIDE.tr.md)

---

## Resumo

Uma versão puramente ponto a ponto de dinheiro eletrônico permitiria enviar pagamentos de uma pessoa para
outra sem passar por nenhuma instituição. As assinaturas digitais resolvem uma parte do problema, mas os
principais benefícios se perdem se ainda for necessário um terceiro de confiança para impedir que uma mesma
moeda seja gasta duas vezes. O SHENT resolve o gasto duplo com uma rede ponto a ponto. A rede registra as
transações em uma blockchain, e cada bloco é selado por uma prova de trabalho. O registro resultante não pode
ser alterado sem refazer esse trabalho. A cadeia mais longa, medida em trabalho acumulado e não em quantidade
de blocos, serve como prova da sequência dos fatos e como prova de que veio do maior poder computacional.
Enquanto a maior parte desse poder estiver nas mãos de nós que não cooperam para atacar a rede, esses nós
geram a cadeia mais longa e superam qualquer atacante. A rede em si exige uma estrutura mínima. As mensagens
são difundidas com o melhor esforço possível, e os nós podem sair e voltar quando quiserem, aceitando a
cadeia com mais trabalho como prova do que aconteceu enquanto estavam fora.

O SHENT é uma moeda comunitária. O bloco gênese diz isso em seu próprio texto:
`SHENT 13/10/2026 - moneda comunitaria sin valor monetario` (moeda comunitária sem valor monetário). Não é
cotado em nenhum mercado, o aplicativo não compra nem vende nada e ninguém promete que vá valer alguma coisa.

---

## 1. Introdução

O dinheiro eletrônico que usamos no dia a dia depende quase sempre de intermediários que processam os
pagamentos. O sistema funciona, mas tem as fraquezas próprias de um modelo baseado na confiança: as transações
podem ser revertidas, os custos de mediação limitam os pagamentos pequenos e cada parte precisa entregar mais
informações do que o necessário.

O que falta é um sistema de pagamento baseado em prova criptográfica em vez de confiança, que permita a duas
pessoas quaisquer negociar diretamente entre si. As transações que são computacionalmente impossíveis de
reverter protegem quem recebe. A rede, que registra a ordem das transações e a sela com trabalho, protege
contra o gasto duplo.

O SHENT é uma implementação dessa ideia: um nó completo com carteira, mineração e rede P2P, que roda em um
computador pessoal comum.

## 2. Contas e transações

O SHENT usa um modelo de contas. Cada conta é identificada por um **endereço**, obtido a partir de uma chave
pública Ed25519: os primeiros 20 bytes do seu SHA-256, codificados em Bech32m com o prefixo da rede
(`shent1…` na rede principal, `tshent1…` na rede de testes, `rshent1…` na local). O endereço não revela a
chave pública até que a conta envie sua primeira transação.

Cada conta tem um **saldo** e um **nonce**, um contador de transações enviadas. Uma transferência contém:

- a chave pública de quem envia,
- o endereço de destino,
- o valor e a taxa,
- o próximo nonce da conta,
- uma altura de expiração opcional,
- um memo opcional de até 80 bytes,
- e uma assinatura Ed25519 sobre tudo o que foi listado acima e sobre o identificador da rede.

O nonce cumpre a função que em outros projetos é cumprida pela referência a saídas anteriores: cada transação
de uma conta só pode ser usada uma vez e em ordem. Duas transações com o mesmo nonce são uma tentativa de gasto
duplo, e a rede aceita no máximo uma. Como a assinatura inclui o identificador da rede, uma transação válida na
rede de testes não serve na principal.

Os valores são armazenados como inteiros: 1 SHENT = 100.000.000 unidades. Não são usados números de ponto
flutuante para o dinheiro.

## 3. Blocos e cadeia

Cada bloco contém um cabeçalho e uma lista de transações. O cabeçalho inclui o hash do bloco anterior, a raiz
Merkle das transações, o carimbo de data/hora, o alvo de dificuldade e o nonce da prova de trabalho. Como cada
bloco inclui o hash do anterior, eles formam uma cadeia. Para alterar um bloco, seria preciso refazer o
trabalho desse bloco e de todos os que vieram depois.

Um bloco só é válido se estas condições forem cumpridas:

- O carimbo de data/hora é maior que a mediana dos 11 blocos anteriores e não está mais de 5 minutos no
  futuro.
- O bloco tem no máximo 1.000.000 bytes.
- A primeira transação é a recompensa do minerador (coinbase) e paga exatamente o que é devido.
- Todas as demais transações têm assinaturas válidas, nonces em ordem e saldo suficiente.

## 4. Prova de trabalho

Para selar um bloco, é preciso encontrar um nonce tal que o hash do cabeçalho fique abaixo do alvo de
dificuldade. O SHENT usa **Argon2id** como função de hash para a prova de trabalho, com 2 MiB de memória por
tentativa. É uma função "memory-hard": cada tentativa precisa de memória além de cálculo, o que aproxima o
desempenho de uma CPU pessoal ao de equipamentos especializados. Não os torna impossíveis: apenas reduz a
vantagem.

Não há atalho para encontrar esse nonce: cada tentativa é independente e a probabilidade de sucesso é a mesma
em cada uma. Verificá-lo, por outro lado, custa um único hash.

A dificuldade é recalculada a cada bloco observando os últimos 60, com o objetivo de que saia **um bloco a
cada 10 minutos em média**, haja poucos ou muitos mineradores. Se mais poder for somado, a dificuldade sobe;
se for retirado, ela desce.

## 5. Rede

Os passos que a rede segue são:

1. As transações novas são difundidas para todos os nós.
2. Cada nó reúne transações novas em um bloco.
3. Cada nó que minera procura a prova de trabalho do seu bloco.
4. Quando um nó a encontra, difunde o bloco para todos os demais.
5. Os demais nós aceitam o bloco somente se todas as suas transações forem válidas e não tiverem sido gastas
   antes.
6. Os nós expressam sua aceitação trabalhando no bloco seguinte, usando o hash do bloco aceito como bloco
   anterior.

Um nó não confia em nenhum outro. Cada bloco e cada transação são verificados localmente com as regras de
consenso, mesmo que cheguem de um nó conhecido. Uma mensagem da rede não pode creditar moedas por si só: somente
um bloco válido dentro da cadeia com mais trabalho altera os saldos.

Para se encontrarem, os nós não dependem de um servidor central. Eles usam três vias ao mesmo tempo:

- a DHT pública do BitTorrent, onde cada nó publica sua porta sob uma chave derivada da rede e do seu bloco
  gênese;
- a descoberta na rede local;
- a troca de endereços entre pares.

Cada endereço encontrado passa por um handshake no qual os dois lados assinam um desafio aleatório com sua
identidade Ed25519. Para que a rede funcione, basta que pelo menos um nó aceite conexões de entrada (porta
TCP 37373 aberta na rede principal). Os nós sem portas abertas podem receber conexões por meio de um relay.

## 6. Incentivo

Por convenção, a primeira transação de cada bloco cria moedas novas que pertencem a quem o minerou. É o
incentivo para sustentar a rede e a única forma de as moedas entrarem em circulação, sem uma autoridade que as
emita.

- A recompensa é de **50 SHENT por bloco**.
- Ela é reduzida à metade a cada **1.000.000 de blocos** (cerca de 19 anos com blocos de 10 minutos).
- A soma nunca ultrapassa **100.000.000 SHENT**.
- Além disso, quem minera recebe as taxas das transações que inclui.

A recompensa é de quem encontra o bloco e não é repartida. Com muitos mineradores, cada um encontra blocos com
uma frequência proporcional à sua parte do poder computacional total. Em média, o resultado é o mesmo que uma
repartição, mas na forma de prêmios inteiros e aleatórios.

O incentivo também ajuda a manter os nós honestos. Quem reunisse mais poder computacional do que todos os
demais juntos teria de escolher entre usá-lo para reescrever a história ou para ganhar moedas novas seguindo as
regras. Vale mais a pena jogar dentro das regras do que minar o sistema do qual depende a sua própria riqueza.

## 7. Cadeias em competição

Pode acontecer de dois nós encontrarem um bloco quase ao mesmo tempo, ou de um grupo de nós ter ficado
desconectado e minerado por conta própria. Passam a existir, então, duas versões da cadeia. Os nós continuam
trabalhando sobre a primeira que receberam, mas guardam a outra caso ela venha a superá-la. O empate é
desfeito quando aparece o bloco seguinte: o ramo com **mais trabalho acumulado** passa a ser o válido em
todos os nós.

As cadeias não se combinam. Os blocos do ramo perdedor são descartados e, com eles, suas recompensas. As
transações que eles continham voltam a ficar pendentes se ainda forem válidas. Um bloco minerado seguindo as
regras mantém sua recompensa enquanto permanecer na cadeia com mais trabalho. Por isso convém esperar algumas
confirmações antes de considerar firme um pagamento grande, e convém minerar com a rede conectada: um nó
isolado pode minerar blocos válidos que depois percam para a cadeia do restante da rede.

Há duas regras de proteção:

- **Profundidade máxima de reorganização:** um nó não substitui mais de 288 blocos já aceitos (cerca de dois
  dias).
- **Pontos de controle:** o programa publicado inclui os hashes de alguns blocos já enterrados na história.

As duas limitam o dano de um ataque longo, em troca de um pouco de confiança no software que se baixa. São uma
decisão de projeto deliberada e conhecida.

## 8. Privacidade

O modelo bancário tradicional obtém privacidade restringindo o acesso à informação. No SHENT, como em
qualquer registro público, todas as transações são visíveis: endereços, valores e momentos. A privacidade é
preservada de outra forma: os endereços não têm nome.

Medidas incluídas:

- **Endereços novos para receber.** A carteira gera um endereço novo cada vez que se pede "Receber". Todos
  saem do mesmo backup, mas, vistos de fora, não têm relação entre si… até serem usados juntos em um
  pagamento.
- **Dandelion++.** As transações próprias passam primeiro por um caminho aleatório de nós e só depois são
  difundidas, para que não seja evidente de qual IP saíram.
- **Tor.** O nó pode se conectar somente por meio de um proxy SOCKS5. Nesse modo, ele não escuta conexões nem
  usa a DHT, a rede local ou o UPnP.

Sobre a criptografia, para que não haja mal-entendidos:

- **As chaves privadas são criptografadas**, somente no computador de cada pessoa.
- **A blockchain não é criptografada**, de propósito. Cada nó precisa poder ler e verificar cada transação.
  Blocos, transferências e saldos são compartilhados quase em tempo real entre os nós, às claras. Não podem
  ser falsificados porque são assinados e selados com trabalho, e não porque estejam ocultos.
- **O protocolo P2P também não é criptografado**, assim como o do Bitcoin. Quem observar a conexão pode ver
  que o computador roda o SHENT e quais dados públicos passam por ela. Para ocultar o IP existe o modo Tor.

## 9. Carteira e backup

As chaves são geradas no próprio computador com o gerador aleatório do sistema operacional. Elas são
guardadas em um arquivo `.shent`, criptografado com uma senha mestra (Argon2id + ChaCha20-Poly1305). A chave
privada nunca sai desse computador nem é enviada pela rede.

- Sem o arquivo e sem a senha, as moedas não podem ser recuperadas. Não existe um mecanismo de recuperação:
  ninguém pode redefinir a senha.
- É preciso guardar uma cópia do arquivo `.shent` fora do computador (um pendrive, uma nuvem) e a senha em um
  lugar seguro.
- O arquivo, a senha e a chave não devem ser compartilhados com ninguém. Nenhum desenvolvedor nem nó vai
  pedi-los nunca.

## 10. Segurança e limites

Um atacante com mais poder computacional do que o restante da rede poderia:

- reescrever os blocos recentes,
- reverter suas próprias transações (gasto duplo),
- impedir que transações alheias sejam confirmadas.

Por outro lado, não poderia:

- criar moedas do nada,
- gastar moedas de uma conta cuja chave não tem,
- mudar as regras.

Os nós honestos rejeitam qualquer bloco inválido, venha de onde vier.

A segurança depende de quanto poder honesto existe. Em uma rede jovem, com poucos mineradores, um único
computador potente pode ter a maioria. A página de Estatísticas do aplicativo mostra quantos endereços
mineraram nos últimos dias e avisa quando um único endereço concentra mais da metade dos blocos.

Este software é fornecido no estado em que se encontra, sem garantias. Nenhum sistema é invulnerável. As
regras, o código e os testes estão à vista para que qualquer pessoa os revise.

## 11. Uso prático

### Instalar (Windows 10/11, 64 bits)

1. Baixar `SHENT-<version>-win-x64.zip` na página de *Releases* do repositório.
2. Verificar o hash com `Get-FileHash .\SHENT-<version>-win-x64.zip -Algorithm SHA256` e compará-lo
   com o arquivo `.sha256` publicado.
3. Descompactar o zip completo e abrir `SHENT.exe`. Não é preciso instalar mais nada.
4. Se o firewall do Windows perguntar, permitir o acesso para que outros nós possam se conectar.

### Primeiros passos

- **Carteira → Criar carteira.** Escolher uma senha mestra e guardar o backup `.shent`.
- **Mineração.** Escolher threads e intensidade e ligar o reator. Se não houver pares conectados, o aplicativo
  avisa: o que for minerado sem rede pode ser descartado.
- **Enviar.** Colar o endereço de destino, o valor e a taxa. Revisar a tela de confirmação antes de assinar.
- **Receber.** Compartilhar o endereço exibido. Ele é público e não permite gastar nada.
- **Idioma.** O seletor com o globo alterna a interface entre 10 idiomas. Os valores são escritos com o
  separador decimal do idioma escolhido. Se um valor for ambíguo, o aplicativo o rejeita em vez de adivinhar.

### Tornar a rede mais forte

Abrir no roteador a porta **TCP 37373** para o computador, ou ativar o UPnP. Cada nó que aceita conexões
facilita a entrada de outros. Para um servidor sem tela existe o nó de console:
`nodo-consola\shent-node.exe help`.

### Compilar a partir do código

Requer o SDK do .NET 10.

```
dotnet build Shent.sln
dotnet test tests/Shent.Core.Tests
dotnet run --project src/Shent.Desktop
```

O release para Windows é gerado com `scripts/publish-windows.ps1`. A documentação técnica da rede está em
[`docs/P2P_INTERNET.md`](../P2P_INTERNET.md).

## 12. Conclusão

Propusemos um sistema de transações eletrônicas que não depende de confiança. As assinaturas digitais dão o
controle das moedas a quem tem a chave. Para impedir o gasto duplo, a rede registra o histórico público de
transações em uma cadeia de prova de trabalho, que se torna impossível de alterar para um atacante enquanto os
nós honestos tiverem a maior parte do poder computacional. A rede é robusta por sua simplicidade: os nós
trabalham ao mesmo tempo com muito pouca coordenação, não precisam se identificar e podem sair e voltar quando
quiserem, aceitando a cadeia com mais trabalho como prova do que aconteceu. Eles votam com seu poder
computacional: aceitam os blocos válidos trabalhando sobre eles e rejeitam os inválidos recusando-se a fazê-lo.
Todas as regras e incentivos necessários são impostos com esse único mecanismo de consenso.

O SHENT existe para que uma comunidade o use, o estude e o melhore.
