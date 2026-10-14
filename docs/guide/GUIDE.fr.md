# SHENT : un système de monnaie électronique communautaire pair-à-pair

**Langues:** [Español](GUIDE.es.md) · [English](GUIDE.en.md) · [Português](GUIDE.pt.md) · [Français](GUIDE.fr.md) · [Deutsch](GUIDE.de.md) · [Italiano](GUIDE.it.md) · [Русский](GUIDE.ru.md) · [中文](GUIDE.zh.md) · [日本語](GUIDE.ja.md) · [Türkçe](GUIDE.tr.md)

---

## Résumé

Une version purement pair-à-pair de la monnaie électronique permettrait d'envoyer des paiements d'une personne
à une autre sans passer par aucune institution. Les signatures numériques résolvent une partie du problème,
mais les principaux avantages sont perdus si un tiers de confiance reste nécessaire pour empêcher qu'une même
pièce soit dépensée deux fois. SHENT résout la double dépense au moyen d'un réseau pair-à-pair. Le réseau
enregistre les transactions dans une chaîne de blocs, et chaque bloc est scellé par une preuve de travail.
L'enregistrement qui en résulte ne peut pas être modifié sans refaire ce travail. La chaîne la plus longue,
mesurée en travail cumulé et non en nombre de blocs, sert de preuve de la séquence des événements et de preuve
qu'elle provient de la plus grande puissance de calcul. Tant que la majeure partie de cette puissance est entre
les mains de nœuds qui ne coopèrent pas pour attaquer le réseau, ces nœuds génèrent la chaîne la plus longue et
distancent tout attaquant. Le réseau lui-même requiert une structure minimale. Les messages sont diffusés au
mieux, et les nœuds peuvent partir et revenir à leur guise, en acceptant la chaîne ayant le plus de travail
comme preuve de ce qui s'est passé pendant leur absence.

SHENT est une monnaie communautaire. Le bloc genèse le dit dans son propre texte :
`SHENT 13/10/2026 - moneda comunitaria sin valor monetario` (monnaie communautaire sans valeur monétaire).
Elle n'est cotée sur aucun marché, l'application n'achète ni ne vend rien et personne ne promet qu'elle vaudra
quoi que ce soit.

---

## 1. Introduction

La monnaie électronique que nous utilisons au quotidien dépend presque toujours d'intermédiaires qui traitent
les paiements. Le système fonctionne, mais il présente les faiblesses propres à un modèle fondé sur la
confiance : les transactions peuvent être annulées, les coûts de médiation limitent les petits paiements et
chaque partie doit fournir plus d'informations que nécessaire.

Ce qu'il faut, c'est un système de paiement fondé sur la preuve cryptographique plutôt que sur la confiance,
qui permette à deux personnes quelconques de traiter directement entre elles. Les transactions qu'il est
impossible, en termes de calcul, d'annuler protègent celui qui reçoit. Le réseau, qui enregistre l'ordre des
transactions et le scelle par du travail, protège contre la double dépense.

SHENT est une mise en œuvre de cette idée : un nœud complet avec portefeuille, minage et réseau P2P, qui
fonctionne sur un ordinateur personnel ordinaire.

## 2. Comptes et transactions

SHENT utilise un modèle de comptes. Chaque compte est identifié par une **adresse**, obtenue à partir d'une
clé publique Ed25519 : les 20 premiers octets de son SHA-256, encodés en Bech32m avec le préfixe du réseau
(`shent1…` sur le réseau principal, `tshent1…` sur le réseau de test, `rshent1…` sur le réseau local).
L'adresse ne révèle pas la clé publique tant que le compte n'a pas envoyé sa première transaction.

Chaque compte possède un **solde** et un **nonce**, un compteur des transactions envoyées. Un transfert
contient :

- la clé publique de l'expéditeur,
- l'adresse de destination,
- le montant et les frais,
- le nonce suivant du compte,
- une hauteur d'expiration facultative,
- un mémo facultatif de 80 octets au maximum,
- et une signature Ed25519 portant sur tout ce qui précède et sur l'identifiant du réseau.

Le nonce remplit la fonction que remplit, dans d'autres conceptions, la référence aux sorties précédentes :
chaque transaction d'un compte ne peut être utilisée qu'une seule fois et dans l'ordre. Deux transactions
ayant le même nonce constituent une tentative de double dépense, et le réseau en accepte au plus une. Comme la
signature inclut l'identifiant du réseau, une transaction valide sur le réseau de test ne sert à rien sur le
réseau principal.

Les montants sont stockés sous forme d'entiers : 1 SHENT = 100 000 000 unités. Aucun nombre à virgule
flottante n'est utilisé pour l'argent.

## 3. Blocs et chaîne

Chaque bloc contient un en-tête et une liste de transactions. L'en-tête comprend le hash du bloc précédent, la
racine Merkle des transactions, l'horodatage, la cible de difficulté et le nonce de la preuve de travail. Comme
chaque bloc inclut le hash du précédent, ils forment une chaîne. Pour modifier un bloc, il faudrait refaire le
travail de ce bloc et de tous ceux qui sont venus après.

Un bloc n'est valide que si ces conditions sont remplies :

- L'horodatage est supérieur à la médiane des 11 blocs précédents et ne se situe pas plus de 5 minutes dans le
  futur.
- Le bloc pèse au maximum 1 000 000 octets.
- La première transaction est la récompense du mineur (coinbase) et paie exactement ce qui est dû.
- Toutes les autres transactions ont des signatures valides, des nonces dans l'ordre et un solde suffisant.

## 4. Preuve de travail

Pour sceller un bloc, il faut trouver un nonce tel que le hash de l'en-tête soit inférieur à la cible de
difficulté. SHENT utilise **Argon2id** comme fonction de hachage pour la preuve de travail, avec 2 MiB de
mémoire par tentative. C'est une fonction « memory-hard » : chaque tentative exige de la mémoire en plus du
calcul, ce qui rapproche les performances d'un CPU personnel de celles d'équipements spécialisés. Cela ne rend
pas ces équipements impossibles : cela réduit seulement leur avantage.

Il n'existe aucun raccourci pour trouver ce nonce : chaque tentative est indépendante et la probabilité de
succès est la même pour chacune. Le vérifier, en revanche, ne coûte qu'un seul hash.

La difficulté est recalculée à chaque bloc en examinant les 60 derniers, avec pour objectif de produire **un
bloc toutes les 10 minutes en moyenne**, qu'il y ait peu ou beaucoup de mineurs. Si de la puissance s'ajoute,
la difficulté augmente ; si elle se retire, elle diminue.

## 5. Réseau

Les étapes suivies par le réseau sont les suivantes :

1. Les nouvelles transactions sont diffusées à tous les nœuds.
2. Chaque nœud rassemble les nouvelles transactions dans un bloc.
3. Chaque nœud qui mine cherche la preuve de travail de son bloc.
4. Lorsqu'un nœud la trouve, il diffuse le bloc à tous les autres.
5. Les autres nœuds n'acceptent le bloc que si toutes ses transactions sont valides et n'ont pas déjà été
   dépensées.
6. Les nœuds expriment leur acceptation en travaillant sur le bloc suivant, en utilisant le hash du bloc
   accepté comme bloc précédent.

Un nœud ne fait confiance à aucun autre. Chaque bloc et chaque transaction sont vérifiés localement selon les
règles de consensus, même lorsqu'ils proviennent d'un nœud connu. Un message du réseau ne peut pas, à lui
seul, créditer des pièces : seul un bloc valide au sein de la chaîne ayant le plus de travail modifie les
soldes.

Pour se trouver, les nœuds ne dépendent d'aucun serveur central. Ils utilisent trois voies en même temps :

- la DHT publique de BitTorrent, où chaque nœud publie son port sous une clé dérivée du réseau et de son bloc
  genèse ;
- la découverte sur le réseau local ;
- l'échange d'adresses entre pairs.

Chaque adresse trouvée passe par un handshake au cours duquel les deux parties signent un défi aléatoire avec
leur identité Ed25519. Pour que le réseau fonctionne, il suffit qu'au moins un nœud accepte les connexions
entrantes (port TCP 37373 ouvert sur le réseau principal). Les nœuds sans port ouvert peuvent recevoir des
connexions par l'intermédiaire d'un relay.

## 6. Incitation

Par convention, la première transaction de chaque bloc crée de nouvelles pièces qui appartiennent à celui qui
l'a miné. C'est l'incitation à soutenir le réseau et la seule façon dont les pièces entrent en circulation,
sans autorité pour les émettre.

- La récompense est de **50 SHENT par bloc**.
- Elle est divisée par deux tous les **1 000 000 blocs** (environ 19 ans avec des blocs de 10 minutes).
- Le total ne dépasse jamais **100 000 000 SHENT**.
- En outre, celui qui mine perçoit les frais des transactions qu'il inclut.

La récompense revient à celui qui trouve le bloc et n'est pas partagée. Avec de nombreux mineurs, chacun trouve
des blocs à une fréquence proportionnelle à sa part de la puissance de calcul totale. En moyenne, le résultat
est le même qu'un partage, mais sous forme de gains entiers et aléatoires.

L'incitation aide aussi les nœuds à rester honnêtes. Quiconque réunirait plus de puissance de calcul que tous
les autres réunis devrait choisir entre l'utiliser pour réécrire l'histoire ou pour gagner de nouvelles pièces
en suivant les règles. Il a davantage intérêt à jouer selon les règles qu'à saper le système dont dépend sa
propre richesse.

## 7. Chaînes concurrentes

Il peut arriver que deux nœuds trouvent un bloc presque au même moment, ou qu'un groupe de nœuds ait été
déconnecté et ait miné de son côté. Il existe alors deux versions de la chaîne. Les nœuds continuent de
travailler sur la première qu'ils ont reçue, mais conservent l'autre au cas où elle viendrait à la dépasser.
L'égalité est rompue lorsque le bloc suivant apparaît : la branche ayant **le plus de travail cumulé** devient
la branche valide sur tous les nœuds.

Les chaînes ne se combinent pas. Les blocs de la branche perdante sont écartés et, avec eux, leurs
récompenses. Les transactions qu'ils contenaient redeviennent en attente si elles sont toujours valides. Un
bloc miné en suivant les règles conserve sa récompense tant qu'il reste dans la chaîne ayant le plus de
travail. C'est pourquoi il convient d'attendre quelques confirmations avant de considérer un paiement
important comme définitif, et de miner avec le réseau connecté : un nœud isolé peut miner des blocs valides
qui perdront ensuite face à la chaîne du reste du réseau.

Il existe deux règles de protection :

- **Profondeur maximale de réorganisation :** un nœud ne remplace pas plus de 288 blocs déjà acceptés (environ
  deux jours).
- **Points de contrôle :** le programme publié inclut les hashes de certains blocs déjà enfouis dans
  l'historique.

Les deux limitent les dégâts d'une attaque longue, en échange d'un peu de confiance dans le logiciel que l'on
télécharge. C'est une décision de conception délibérée et connue.

## 8. Confidentialité

Le modèle bancaire traditionnel assure la confidentialité en restreignant l'accès à l'information. Dans SHENT,
comme dans tout registre public, toutes les transactions sont visibles : adresses, montants et moments. La
confidentialité est préservée d'une autre manière : les adresses ne portent pas de nom.

Mesures incluses :

- **De nouvelles adresses pour recevoir.** Le portefeuille génère une nouvelle adresse chaque fois que l'on
  demande « Recevoir ». Toutes proviennent de la même sauvegarde, mais, vues de l'extérieur, elles n'ont
  aucun lien entre elles… jusqu'à ce qu'elles soient utilisées ensemble dans un paiement.
- **Dandelion++.** Les transactions propres passent d'abord par un chemin aléatoire de nœuds et ne sont
  diffusées qu'ensuite, afin qu'il ne soit pas évident de savoir de quelle IP elles sont parties.
- **Tor.** Le nœud peut se connecter uniquement à travers un proxy SOCKS5. Dans ce mode, il n'écoute pas de
  connexions et n'utilise ni la DHT, ni le réseau local, ni UPnP.

Au sujet du chiffrement, pour éviter tout malentendu :

- **Les clés privées sont bien chiffrées**, uniquement sur l'ordinateur de chaque personne.
- **La chaîne de blocs n'est pas chiffrée**, volontairement. Chaque nœud doit pouvoir lire et vérifier chaque
  transaction. Blocs, transferts et soldes sont partagés presque en temps réel entre les nœuds, en clair. Ils
  ne peuvent pas être falsifiés parce qu'ils sont signés et scellés par du travail, et non parce qu'ils sont
  cachés.
- **Le protocole P2P n'est pas chiffré non plus**, tout comme celui de Bitcoin. Quiconque observe la
  connexion peut voir que l'ordinateur exécute SHENT et quelles données publiques transitent. Pour masquer
  l'IP, il existe le mode Tor.

## 9. Portefeuille et sauvegarde

Les clés sont générées sur l'ordinateur lui-même avec le générateur aléatoire du système d'exploitation. Elles
sont conservées dans un fichier `.shent`, chiffré avec un mot de passe principal (Argon2id +
ChaCha20-Poly1305). La clé privée ne quitte jamais cet ordinateur et n'est jamais envoyée sur le réseau.

- Sans le fichier et sans le mot de passe, les pièces ne peuvent pas être récupérées. Il n'existe aucun
  mécanisme de récupération : personne ne peut réinitialiser le mot de passe.
- Il faut conserver une copie du fichier `.shent` hors de l'ordinateur (une clé USB, un cloud) et le mot de
  passe dans un endroit sûr.
- Le fichier, le mot de passe et la clé ne doivent être partagés avec personne. Aucun développeur ni aucun
  nœud ne les demandera jamais.

## 10. Sécurité et limites

Un attaquant disposant de plus de puissance de calcul que le reste du réseau pourrait :

- réécrire les blocs récents,
- annuler ses propres transactions (double dépense),
- empêcher la confirmation des transactions d'autrui.

En revanche, il ne pourrait pas :

- créer des pièces à partir de rien,
- dépenser les pièces d'un compte dont il n'a pas la clé,
- changer les règles.

Les nœuds honnêtes rejettent tout bloc invalide, d'où qu'il vienne.

La sécurité dépend de la quantité de puissance honnête. Dans un réseau jeune, avec peu de mineurs, un seul
ordinateur puissant peut détenir la majorité. La page Statistiques de l'application indique combien
d'adresses ont miné au cours des derniers jours et avertit lorsqu'une seule concentre plus de la moitié des
blocs.

Ce logiciel est fourni tel quel, sans garantie. Aucun système n'est invulnérable. Les règles, le code et les
tests sont visibles de tous pour que chacun puisse les examiner.

## 11. Utilisation pratique

### Installer (Windows 10/11, 64 bits)

1. Télécharger `SHENT-<version>-win-x64.zip` depuis la page *Releases* du dépôt.
2. Vérifier le hash avec `Get-FileHash .\SHENT-<version>-win-x64.zip -Algorithm SHA256` et le comparer
   au fichier `.sha256` publié.
3. Décompresser l'intégralité du zip et ouvrir `SHENT.exe`. Il n'y a rien d'autre à installer.
4. Si le pare-feu Windows le demande, autoriser l'accès pour que d'autres nœuds puissent se connecter.

### Premiers pas

- **Portefeuille → Créer un portefeuille.** Choisir un mot de passe principal et conserver la sauvegarde
  `.shent`.
- **Minage.** Choisir les threads et l'intensité, puis allumer le réacteur. S'il n'y a aucun pair connecté,
  l'application le signale : ce qui est miné sans réseau peut être écarté.
- **Envoyer.** Coller l'adresse de destination, le montant et les frais. Vérifier l'écran de confirmation
  avant de signer.
- **Recevoir.** Partager l'adresse affichée. Elle est publique et ne permet de rien dépenser.
- **Langue.** Le sélecteur avec le globe fait passer l'interface d'une langue à l'autre parmi 10 langues.
  Les montants s'écrivent avec le séparateur décimal de la langue choisie. Si un montant est ambigu,
  l'application le refuse au lieu de deviner.

### Renforcer le réseau

Ouvrir sur le routeur le port **TCP 37373** vers l'ordinateur, ou activer UPnP. Chaque nœud qui accepte des
connexions facilite l'arrivée d'autres nœuds. Pour un serveur sans écran, il existe le nœud en console :
`nodo-consola\shent-node.exe help`.

### Compiler depuis le code source

Nécessite le SDK .NET 10.

```
dotnet build Shent.sln
dotnet test tests/Shent.Core.Tests
dotnet run --project src/Shent.Desktop
```

La release pour Windows est générée avec `scripts/publish-windows.ps1`. La documentation technique du réseau
se trouve dans [`docs/P2P_INTERNET.md`](../P2P_INTERNET.md).

## 12. Conclusion

Nous avons proposé un système de transactions électroniques qui ne repose pas sur la confiance. Les signatures
numériques donnent le contrôle des pièces à celui qui détient la clé. Pour empêcher la double dépense, le
réseau enregistre l'historique public des transactions dans une chaîne de preuve de travail, qui devient
impossible à modifier pour un attaquant tant que les nœuds honnêtes détiennent la majeure partie de la
puissance de calcul. Le réseau est robuste par sa simplicité : les nœuds travaillent tous en même temps avec
très peu de coordination, n'ont pas besoin de s'identifier et peuvent partir et revenir à leur guise, en
acceptant la chaîne ayant le plus de travail comme preuve de ce qui s'est passé. Ils votent avec leur
puissance de calcul : ils acceptent les blocs valides en travaillant dessus et rejettent les blocs invalides
en refusant de le faire. Toutes les règles et incitations nécessaires sont imposées par ce seul mécanisme de
consensus.

SHENT existe pour qu'une communauté l'utilise, l'étudie et l'améliore.
