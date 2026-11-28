# Measuring speed

Applies when a change is made for speed, when a change may cost speed on a
path that matters, and when a figure about speed is written into a
document or a change description. Language-neutral; a repository that
holds speed as a design constraint says so in a rule of its own, and this
says how its claims are measured.

**A change made for speed comes with the measurement that shows it.**
"Should be faster" is not a reason for the code it costs. The same holds
in reverse: a change that makes a path slower says so and says why it is
worth it.

## Two measurements, and what each can say

| Measurement | Says | Cannot say |
|---|---|---|
| a benchmark of the code changed | whether the change is faster, and by what ratio | whether a program is |
| the program, or its tests, before and after | what a user gains | why |

Both are reported where both can be made. They often disagree in size: a
procedure made a third faster may move a whole run by a percent, or by
nothing that can be told apart from the run's own spread.

## The benchmark

- **In pairs, as a ratio.** The candidate and what is there today are run
  alternately in short slices, and the figure is the ratio of the two with
  the range the pairs fell in. A machine's speed is not steady, a virtual
  one's least of all; a pair meets the same machine, where two runs a
  minute apart do not.
- **A control before the change.** Where the two forms lie in different
  places, one in the library and one in the benchmark, measure the form
  that is there today against a copy of itself placed as the candidate
  will be. The ratio should be 1. What it is instead is what placement
  alone is worth, and is taken out of the candidate's figure.
- **Inputs the compiler cannot see through.** A length or a count read
  from a field, not written where it is used, unless the case being
  measured is the one the compiler can see.
- **The shapes are the program's.** Sizes and values taken from where the
  code is called, and named so in the benchmark.
- **A test that both forms give the same result**, bit for bit where
  numbers are concerned, beside the benchmark.
- **A candidate that does not prove faster is deleted with its
  benchmark**, and the answer recorded where it shaped the code.

## The program

- **Two builds that differ only in the change**, kept side by side, and
  run alternately: before, after, before, after. Never all of one and then
  all of the other.
- **Every pair is reported**, not only the mean. Pairs that disagree in
  direction say the difference is inside the spread, and the description
  says so in those words.
- **One at a time for a small difference.** Runs in parallel disturb one
  another by more than a few percent.
- **A part that reads slower is run again before it is believed.** Several
  more turns each way, of that part alone; most such readings are its own
  spread.
- **The results are checked on the build that was timed.** A faster build
  that gives another answer is not a faster build.

## What is written down

- **The figure, how it was taken, and on what:** the ratio and its range,
  how many pairs, and for a figure about a whole program the machine and
  the day, since it is not kept up.
- **What it does not show.** "Measured on the procedure and not on a whole
  run" is a finding, and is written as one.
- **What was measured and not taken**, with its figure, where the choice
  is described: the next reader is spared measuring it again.
- **A profile says where to look, not what a change is worth.** A share
  of the time is an upper bound on a gain, and time spent in a small
  procedure written in line is counted with its caller.

## Review

| Finding | Rule |
|---|---|
| a change made for speed with no figure, or with "should be faster" | a change made for speed comes with the measurement that shows it |
| a figure from two runs made one after the other, or with no range | *The benchmark*: in pairs, as a ratio |
| a benchmark of one procedure given as what a program gains | *Two measurements, and what each can say* |
| a benchmark whose sizes are not the program's, or whose inputs are constants in sight of the compiler | *The benchmark* |
| a mean with the pairs left out, or pairs that disagree in direction called a gain | *The program* |
| a faster build whose results were not checked | *The program* |
| a candidate that was not taken, left in the code with its benchmark | *The benchmark* |
| a figure in a document with no account of how it was taken | *What is written down* |
